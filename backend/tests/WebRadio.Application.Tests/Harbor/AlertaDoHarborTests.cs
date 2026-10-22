using System.Text.Json;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Harbor;
using WebRadio.Application.Features.Transmissao;
using WebRadio.Application.Tests.Auth;
using WebRadio.Application.Tests.Transmissao;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using WebRadio.Infrastructure.Seguranca;
using Xunit;

namespace WebRadio.Application.Tests.Harbor;

/// <summary>E2-F12 (risco aceito 3): alerta de falhas de credencial no harbor, fora do caminho do 401 e sem bloquear nada.</summary>
public class AlertaDoHarborTests
{
    private const string Ip = "203.0.113.77";

    private readonly ConsultasFalsas _consultas = new();
    private readonly RelogioFalso _relogio = new(AuthFixture.Agora);
    private readonly string _nomeDoBanco = Guid.NewGuid().ToString();
    private readonly HarborOptions _opcoes = new();
    private readonly LogCapturado<AutenticarNoHarborHandler> _logAuth = new();
    private readonly LogCapturado<ThrottleDoHarbor> _logThrottle = new();
    private readonly LogCapturado<AlertaDoHarbor> _logAlerta = new();
    private readonly LogCapturado<FilaDeAlertasDoHarbor> _logFila = new();
    private readonly FilaDeAlertasDoHarbor _fila;
    private IRequestThrottle _contadores;
    private Func<RadioDbContext, IOutbox> _outbox = db => new OutboxEf(db);

    private readonly Guid _id = Guid.NewGuid();
    private readonly Guid _donoId = Guid.NewGuid();
    private readonly string _segredo;

    public AlertaDoHarborTests()
    {
        _fila = new FilaDeAlertasDoHarbor(_logFila);
        _contadores = new MemoryRequestThrottle(_relogio);
        var g = SegredoDeTransmissao.Gerar();
        _segredo = g.Segredo;
        _consultas.Credenciais[_id] = new CredencialDoHarbor(Guid.NewGuid(), g.Salt, g.Hash);
        _consultas.Donos[_id] = new DonoDaCredencial(_donoId, "Ana Locutora", Ativa: true);
        _consultas.Janelas[_id] = new JanelaDoHarbor(Guid.NewGuid(), AuthFixture.Agora.AddHours(1));
    }

    private AutenticarNoHarborHandler Handler(FilaDeAlertasDoHarbor? fila = null) => new(
        _consultas,
        new ThrottleDoHarbor(_contadores, new IpHasher(new byte[32]), Options.Create(_opcoes), _logThrottle),
        fila ?? _fila,
        new IpHasher(new byte[32]), _logAuth);

    private Task<JanelaDoHarbor?> Falha(Guid? id = null, string ip = Ip, FilaDeAlertasDoHarbor? fila = null) =>
        Handler(fila).Handle(new AutenticarNoHarborCommand((id ?? _id).ToString(), SegredoDeTransmissao.Gerar().Segredo, ip), CancellationToken.None);

    /// <summary>Falhas do identificador, cada uma de um IP diferente (nem o par nem o IP chegam ao limite).</summary>
    private async Task Falhas(int quantas, Guid? id = null, FilaDeAlertasDoHarbor? fila = null)
    {
        for (var i = 0; i < quantas; i++) await Falha(id, $"192.0.2.{i % 250 + 1}", fila);
    }

    /// <summary>Falhas com identificadores desconhecidos (nenhuma credencial), cada um de um IP diferente.</summary>
    private async Task FalhasDeDesconhecidos(int quantas, FilaDeAlertasDoHarbor? fila = null)
    {
        for (var i = 0; i < quantas; i++) await Falha(Guid.NewGuid(), $"198.51.{i / 250}.{i % 250 + 1}", fila);
    }

    /// <summary>O serviço em segundo plano: esvazia a fila, cada alerta num <c>DbContext</c> próprio (mesmo banco em memória).</summary>
    private async Task Drenar(FilaDeAlertasDoHarbor? fila = null)
    {
        fila ??= _fila;
        while (fila.TentarLer(out var pendente))
        {
            var db = AuthFixture.NovoDb(_nomeDoBanco);
            await new AlertaDoHarbor(_consultas, _outbox(db), db, _logAlerta).ProcessarAsync(pendente!, CancellationToken.None);
        }
    }

    private List<(string[] Grupos, JsonElement Dados)> Alertas() => AuthFixture.NovoDb(_nomeDoBanco).EventosOutbox.ToList()
        .Where(e => e.Tipo == EventosDoOutbox.AlertaHarborFalhas)
        .Select(e =>
        {
            var envelope = JsonDocument.Parse(e.Payload).RootElement;
            return (envelope.GetProperty("grupos").EnumerateArray().Select(g => g.GetString()!).ToArray(), envelope.GetProperty("dados").Clone());
        }).ToList();

    private string[] Tipos() => [.. Alertas().Select(a => a.Dados.GetProperty("tipo").GetString()!)];

    [Fact]
    public async Task A_decima_falha_grava_exatamente_um_alerta_de_credencial_e_a_11a_na_mesma_janela_nao()
    {
        await Falhas(ThrottleDoHarbor.AlertaPorIdentificador - 1);
        await Drenar();
        Assert.Empty(Alertas());

        await Falhas(1);
        await Drenar();
        var (grupos, dados) = Assert.Single(Alertas());
        Assert.Equal(["admins", $"usuario:{_donoId:D}"], grupos); // destino explícito, nunca "todos"
        Assert.Equal("credencial", dados.GetProperty("tipo").GetString());
        Assert.Equal(10, dados.GetProperty("falhas").GetInt32());
        Assert.Equal(5, dados.GetProperty("janelaMinutos").GetInt32());
        Assert.Equal(_donoId, dados.GetProperty("locutorId").GetGuid());
        Assert.Equal("Ana Locutora", dados.GetProperty("locutorNome").GetString());
        Assert.True(dados.GetProperty("credencialAtiva").GetBoolean());

        await Falhas(5);
        await Drenar();
        Assert.Single(Alertas());
    }

    [Fact]
    public async Task Credencial_revogada_ainda_nao_apagada_gera_o_alerta_com_credencialAtiva_false()
    {
        // O BUTT com o segredo antigo: gerar credencial nova cria identificador novo, e o antigo fica revogado.
        _consultas.Credenciais.Remove(_id); // o auth não acha credencial ATIVA: toda tentativa é falha de credencial
        _consultas.Donos[_id] = new DonoDaCredencial(_donoId, "Ana Locutora", Ativa: false);

        await Falhas(ThrottleDoHarbor.AlertaPorIdentificador);
        await Drenar();

        var (grupos, dados) = Assert.Single(Alertas());
        Assert.Equal(["admins", $"usuario:{_donoId:D}"], grupos);
        Assert.Equal("credencial", dados.GetProperty("tipo").GetString());
        Assert.False(dados.GetProperty("credencialAtiva").GetBoolean());
    }

    [Fact]
    public async Task Identificador_que_nao_e_de_credencial_nenhuma_nao_gera_alerta_de_credencial()
    {
        await Falhas(ThrottleDoHarbor.AlertaPorIdentificador + 2, Guid.NewGuid());
        await Drenar();

        Assert.Empty(Alertas());
        Assert.Contains(_logThrottle.Linhas, l => l.Contains("HARBOR_ALERTA_IDENTIFICADOR"));
    }

    [Fact]
    public async Task O_auth_so_enfileira_a_consulta_do_dono_e_a_gravacao_acontecem_depois()
    {
        await Falhas(ThrottleDoHarbor.AlertaPorIdentificador);

        Assert.Equal(0, _consultas.ConsultasAoDono);
        Assert.Empty(Alertas());

        await Drenar();
        Assert.Equal(1, _consultas.ConsultasAoDono);
        Assert.Single(Alertas());
    }

    [Fact]
    public async Task Na_janela_seguinte_o_alerta_volta_a_sair_uma_vez()
    {
        await Falhas(ThrottleDoHarbor.AlertaPorIdentificador);
        _relogio.UtcNow = _relogio.UtcNow.Add(ThrottleDoHarbor.Janela).AddSeconds(1);

        await Falhas(ThrottleDoHarbor.AlertaPorIdentificador + 3);
        await Drenar();

        Assert.Equal(2, Alertas().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cinquenta_falhas_de_qualquer_identificador_geram_um_unico_alerta_global_nos_dois_modos(bool atrasDoProxy)
    {
        _opcoes.AtrasDoProxy = atrasDoProxy;

        await FalhasDeDesconhecidos(ThrottleDoHarbor.AlertaGlobal - 1);
        await Drenar();
        Assert.Empty(Alertas());

        await FalhasDeDesconhecidos(1);
        await Drenar();
        var (grupos, dados) = Assert.Single(Alertas());
        Assert.Equal(["admins"], grupos);
        Assert.Equal("global", dados.GetProperty("tipo").GetString());
        Assert.Equal(50, dados.GetProperty("falhas").GetInt32());
        Assert.Equal(5, dados.GetProperty("janelaMinutos").GetInt32());
        Assert.False(dados.TryGetProperty("locutorId", out _));
        Assert.False(dados.TryGetProperty("locutorNome", out _));
        Assert.False(dados.TryGetProperty("credencialAtiva", out _));
        Assert.Contains(_logThrottle.Linhas, l => l.Contains("HARBOR_ALERTA_GLOBAL"));

        await FalhasDeDesconhecidos(10);
        await Drenar();
        Assert.Single(Alertas());
    }

    [Fact]
    public async Task O_global_soma_conhecidas_e_desconhecidas_e_os_dois_alertas_convivem()
    {
        await Falhas(ThrottleDoHarbor.AlertaPorIdentificador); // a 10ª: credencial
        await FalhasDeDesconhecidos(ThrottleDoHarbor.AlertaGlobal - ThrottleDoHarbor.AlertaPorIdentificador); // a 50ª no total: global
        await Drenar();

        Assert.Equal(["credencial", "global"], Tipos());
    }

    [Fact]
    public async Task Formato_invalido_bloqueado_e_fora_da_janela_nao_contam_no_global()
    {
        var foraDaJanela = Guid.NewGuid();
        var g = SegredoDeTransmissao.Gerar();
        _consultas.Credenciais[foraDaJanela] = new CredencialDoHarbor(Guid.NewGuid(), g.Salt, g.Hash);

        // 10 falhas contadas do mesmo par; as 30 seguintes batem no bloqueio e não contam.
        for (var i = 0; i < 40; i++) await Falha(_id, Ip);
        // Formato inválido (usuário ou senha): 401 sem tocar no throttle.
        for (var i = 0; i < 100; i++)
        {
            await Handler().Handle(new AutenticarNoHarborCommand("nao-e-guid", _segredo, $"203.0.113.{i + 1}"), CancellationToken.None);
            await Handler().Handle(new AutenticarNoHarborCommand(_id.ToString(), "curta", $"203.0.113.{i + 1}"), CancellationToken.None);
        }

        // Credencial certa fora da janela: 401, mas não é falha de credencial.
        for (var i = 0; i < 100; i++)
            await Handler().Handle(new AutenticarNoHarborCommand(foraDaJanela.ToString(), g.Segredo, $"203.0.114.{i % 250 + 1}"), CancellationToken.None);

        // Só 10 contadas até aqui: com mais 39 são 49, e a 50ª dispara.
        await FalhasDeDesconhecidos(ThrottleDoHarbor.AlertaGlobal - ThrottleDoHarbor.AlertaPorIdentificador - 1);
        await Drenar();
        Assert.DoesNotContain("global", Tipos());

        await FalhasDeDesconhecidos(1);
        await Drenar();
        Assert.Contains("global", Tipos());
    }

    [Fact]
    public async Task Atras_do_proxy_o_alerta_de_credencial_vale_e_nada_bloqueia()
    {
        _opcoes.AtrasDoProxy = true;

        await Falhas(ThrottleDoHarbor.AlertaPorIdentificador + 2);
        await Drenar();

        Assert.Equal(["credencial"], Tipos());
        var certo = await Handler().Handle(new AutenticarNoHarborCommand(_id.ToString(), _segredo, Ip), CancellationToken.None);
        Assert.NotNull(certo); // o alerta não bloqueia: o locutor com o segredo certo entra
    }

    [Fact]
    public async Task Falhas_e_consumidores_concorrentes_gravam_exatamente_um_alerta_por_chave()
    {
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => Falha(_id, $"192.0.2.{i + 1}"))));
        await Task.WhenAll(Enumerable.Range(0, 80).Select(i => Task.Run(() => Falha(Guid.NewGuid(), $"198.51.100.{i + 1}"))));

        await Task.WhenAll(Task.Run(() => Drenar()), Task.Run(() => Drenar()));

        Assert.Equal(["credencial", "global"], Tipos().Order().ToArray());
    }

    [Fact]
    public async Task Contador_fora_nao_gera_alerta_e_o_log_de_indisponivel_continua()
    {
        _contadores = new ThrottleQueFalha();

        await Falhas(ThrottleDoHarbor.AlertaGlobal + 5);
        await Drenar();

        Assert.Empty(Alertas());
        Assert.Contains(_logThrottle.Linhas, l => l.Contains("HARBOR_THROTTLE_INDISPONIVEL"));
    }

    [Fact]
    public async Task Se_o_outbox_lanca_na_decima_falha_a_resposta_e_a_mesma_401_o_log_e_HARBOR_ALERTA_FALHOU_e_nada_e_regravado()
    {
        _outbox = _ => new OutboxQueFalha();

        await Falhas(ThrottleDoHarbor.AlertaPorIdentificador - 1);
        var nona = await Falha();
        var decima = await Falha();
        await Drenar();

        Assert.Null(nona);
        Assert.Null(decima); // o mesmo 401 sem corpo: nenhuma exceção chega ao auth
        Assert.Contains(_logAlerta.Linhas, l => l.Contains("HARBOR_ALERTA_FALHOU"));

        _outbox = db => new OutboxEf(db); // o banco volta: a 11ª não regrava o que se perdeu
        await Falhas(3);
        await Drenar();
        Assert.Empty(Alertas());
    }

    [Fact]
    public async Task Fila_cheia_descarta_o_alerta_e_loga_HARBOR_ALERTA_FALHOU_e_o_auth_responde_o_mesmo_401()
    {
        var pequena = new FilaDeAlertasDoHarbor(_logFila, capacidade: 1);

        // O alerta de credencial (10ª falha do dono) ocupa a única vaga; ninguém esvazia a fila.
        for (var i = 1; i <= ThrottleDoHarbor.AlertaPorIdentificador; i++)
            Assert.Null(await Falha(_id, $"192.0.2.{i}", pequena));
        Assert.DoesNotContain(_logFila.Linhas, l => l.Contains("HARBOR_ALERTA_FALHOU"));

        // O global (50ª no total) não cabe: some com log, e todas as respostas continuam o mesmo 401.
        for (var i = 0; i < ThrottleDoHarbor.AlertaGlobal - ThrottleDoHarbor.AlertaPorIdentificador; i++)
            Assert.Null(await Falha(Guid.NewGuid(), $"198.51.100.{i + 1}", pequena));

        Assert.Contains(_logFila.Linhas, l => l.Contains("HARBOR_ALERTA_FALHOU"));
        await Drenar(pequena);
        Assert.Equal(["credencial"], Tipos());
    }

    [Fact]
    public void A_fila_tem_100_itens_e_o_consumidor_no_maximo_30_consultas_por_minuto()
    {
        Assert.Equal(100, FilaDeAlertasDoHarbor.CapacidadePadrao);
        Assert.Equal(30, FilaDeAlertasDoHarbor.MaximoDeConsultasPorMinuto);
        Assert.Equal(TimeSpan.FromSeconds(2), FilaDeAlertasDoHarbor.EspacoEntreConsultas);
        Assert.True(AlertaDoHarbor.ConsultaODono(new AlertaPendente(AlertasDaFalha.Identificador, Guid.NewGuid())));
        Assert.False(AlertaDoHarbor.ConsultaODono(new AlertaPendente(AlertasDaFalha.Global, Guid.NewGuid())));
    }

    [Fact]
    public async Task Payload_e_logs_nao_levam_segredo_identificador_nem_ip()
    {
        // Um IP diferente por falha, todos de 203.0.113.x: o alerta de credencial (10 do dono) e o global (50 no total) saem juntos.
        var segredos = new List<string>();
        for (var i = 0; i < ThrottleDoHarbor.AlertaGlobal; i++)
        {
            var errado = SegredoDeTransmissao.Gerar().Segredo;
            segredos.Add(errado);
            var id = i < ThrottleDoHarbor.AlertaPorIdentificador ? _id : Guid.NewGuid();
            await Handler().Handle(new AutenticarNoHarborCommand(id.ToString(), errado, $"203.0.113.{i + 1}"), CancellationToken.None);
        }

        await Drenar();
        var payloads = string.Join("\n", AuthFixture.NovoDb(_nomeDoBanco).EventosOutbox.ToList().Select(e => e.Payload));
        Assert.Equal(["credencial", "global"], Tipos());
        Assert.DoesNotContain(_id.ToString(), payloads, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("203.0.113", payloads);
        Assert.DoesNotContain(_segredo, payloads);
        Assert.All(segredos, s => Assert.DoesNotContain(s, payloads));

        // Nos logs: o identificador pode aparecer (HARBOR_ALERTA_IDENTIFICADOR já existia); segredo e IP inteiro, nunca.
        var logs = string.Join("\n", _logAuth.Linhas.Concat(_logThrottle.Linhas).Concat(_logAlerta.Linhas).Concat(_logFila.Linhas));
        Assert.Matches(@"203\.0\.113\.0/24", logs);
        Assert.DoesNotMatch(@"203\.0\.113\.[1-9]", logs);
        Assert.DoesNotContain(_segredo, logs);
        Assert.All(segredos, s => Assert.DoesNotContain(s, logs));
        Assert.DoesNotContain(_logThrottle.Linhas.Where(l => l.Contains("HARBOR_ALERTA_GLOBAL")), l => l.Contains(_id.ToString()));
    }

    private sealed class OutboxQueFalha : IOutbox
    {
        public void EnfileirarParaTodos(string tipo, object carga) => throw new InvalidOperationException("banco fora (simulado)");
        public void EnfileirarParaGrupos(IReadOnlyCollection<string> grupos, string tipo, object carga) => throw new InvalidOperationException("banco fora (simulado)");
        public void EnfileirarSessoesRevogadas(Guid usuarioId) => throw new InvalidOperationException("banco fora (simulado)");
    }
}
