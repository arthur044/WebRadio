using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Harbor;
using WebRadio.Application.Features.Transmissao;
using WebRadio.Application.Tests.Auth;
using WebRadio.Application.Tests.Transmissao;
using WebRadio.Infrastructure.Seguranca;
using Xunit;

namespace WebRadio.Application.Tests.Harbor;

public sealed class ConsultasFalsas : IConsultasDoHarbor
{
    public Dictionary<Guid, CredencialDoHarbor> Credenciais { get; } = [];
    public Dictionary<Guid, JanelaDoHarbor> Janelas { get; } = [];
    public Dictionary<Guid, DonoDaCredencial> Donos { get; } = [];
    public List<Guid> Usos { get; } = [];
    public int Chamadas { get; private set; }

    public Task<CredencialDoHarbor?> CredencialAtivaAsync(Guid identificador, CancellationToken ct)
    {
        Chamadas++;
        return Task.FromResult(Credenciais.GetValueOrDefault(identificador));
    }

    public Task<JanelaDoHarbor?> JanelaAsync(Guid identificador, CancellationToken ct)
    {
        Chamadas++;
        return Task.FromResult(Janelas.TryGetValue(identificador, out var j) ? j : null);
    }

    public int ConsultasAoDono { get; private set; }

    public Task<DonoDaCredencial?> DonoDaCredencialAsync(Guid identificador, CancellationToken ct)
    {
        ConsultasAoDono++;
        return Task.FromResult(Donos.GetValueOrDefault(identificador));
    }

    public Task RegistrarUsoAsync(Guid credencialId, CancellationToken ct)
    {
        Usos.Add(credencialId);
        return Task.CompletedTask;
    }
}

/// <summary>06 "Endpoints internos" e "Throttle do harbor/auth" (E2-F05).</summary>
public class AutenticarNoHarborTests
{
    private const string IpA = "203.0.113.77";
    private const string IpB = "198.51.100.9";

    private readonly ConsultasFalsas _consultas = new();
    private readonly RelogioFalso _relogio = new(AuthFixture.Agora);
    private readonly LogCapturado<AutenticarNoHarborHandler> _log = new();
    private readonly HarborOptions _opcoes = new();
    private IRequestThrottle _contadores;

    private readonly Guid _id = Guid.NewGuid();
    private readonly string _segredo;
    private readonly Guid _credencialId = Guid.NewGuid();
    private readonly JanelaDoHarbor _janela = new(Guid.NewGuid(), AuthFixture.Agora.AddHours(1));

    public AutenticarNoHarborTests()
    {
        _contadores = new MemoryRequestThrottle(_relogio);
        var g = SegredoDeTransmissao.Gerar();
        _segredo = g.Segredo;
        _consultas.Credenciais[_id] = new CredencialDoHarbor(_credencialId, g.Salt, g.Hash);
        _consultas.Janelas[_id] = _janela;
    }

    private AutenticarNoHarborHandler Handler() =>
        new(_consultas, new ThrottleDoHarbor(_contadores, new IpHasher(new byte[32]), Options.Create(_opcoes), new LogCapturado<ThrottleDoHarbor>()), SemAlerta(), new IpHasher(new byte[32]), _log);

    /// <summary>Os testes do throttle não olham os alertas (E2-F12 tem testes próprios): uma fila que ninguém esvazia.</summary>
    internal static FilaDeAlertasDoHarbor SemAlerta() => new(new LogCapturado<FilaDeAlertasDoHarbor>());

    private Task<JanelaDoHarbor?> Auth(string? usuario, string? senha, string? ip = IpA) =>
        Handler().Handle(new AutenticarNoHarborCommand(usuario, senha, ip), CancellationToken.None);

    private Task<JanelaDoHarbor?> Certo(string ip = IpA) => Auth(_id.ToString(), _segredo, ip);

    /// <summary>
    /// Mesmo formato (43 caracteres de base64url), valor sempre diferente: troca o primeiro caractere por outro. Um "x" fixo na frente
    /// reproduzia o segredo verdadeiro em 1 de cada 64 execuções (quando o segredo sorteado já começava com "x") e o teste falhava.
    /// </summary>
    internal static string Adulterar(string segredo) => (segredo[0] == 'x' ? 'y' : 'x') + segredo[1..];

    private Task<JanelaDoHarbor?> Errado(Guid? id = null, string ip = IpA) => Auth((id ?? _id).ToString(), SegredoDeTransmissao.Gerar().Segredo, ip);

    [Fact]
    public async Task Credencial_certa_na_janela_devolve_programa_e_fim_e_registra_o_uso()
    {
        Assert.Equal(_janela, await Certo());
        Assert.Equal([_credencialId], _consultas.Usos);
        Assert.Contains(_log.Linhas, l => l.Contains("HARBOR_AUTH_OK") && l.Contains(_id.ToString()));
    }

    public static TheoryData<string?, string?> FormatosInvalidos() => new()
    {
        { "nao-e-guid", null },
        { "3F2B1C4D-5E6F-4A7B-8C9D-0E1F2A3B4C5D", null },
        { "../3f2b1c4d-5e6f-4a7b-8c9d-0e1f2a3b4c", null },
        { "3f2b1c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d\n", null },
        { "\"3f2b1c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5\"", null },
        { null, "senha-com-espaco e acento á-xxxxxxxxxxxxxxxxx" },
        { null, "curta" },
        { null, "" },
    };

    [Theory]
    [MemberData(nameof(FormatosInvalidos))]
    public async Task Formato_invalido_e_401_sem_banco_sem_throttle_e_sem_o_valor_no_log(string? usuario, string? senha)
    {
        usuario ??= _id.ToString();
        senha ??= _segredo;

        Assert.Null(await Auth(usuario, senha));

        Assert.Equal(0, _consultas.Chamadas);
        var log = string.Join("\n", _log.Linhas);
        if (usuario.Length > 0 && usuario != _id.ToString()) Assert.DoesNotContain(usuario.Trim('\n'), log);
        if (senha.Length > 0) Assert.DoesNotContain(senha, log);
        Assert.Contains("formato_invalido", log);
    }

    [Fact]
    public async Task Segredo_no_campo_usuario_nao_vai_para_o_log()
    {
        Assert.Null(await Auth(_segredo, _segredo));

        var log = string.Join("\n", _log.Linhas);
        Assert.DoesNotContain(_segredo, log);
        Assert.Contains("usuario_invalido (43 caracteres)", log);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nao-e-ip")]
    [InlineData(null)]
    public async Task Endereco_ip_ilegivel_e_401_sem_banco(string? ip)
    {
        Assert.Null(await Auth(_id.ToString(), _segredo, ip));
        Assert.Equal(0, _consultas.Chamadas);
    }

    [Fact]
    public async Task Identificador_inexistente_segredo_errado_e_fora_da_janela_sao_401()
    {
        Assert.Null(await Errado(Guid.NewGuid()));
        Assert.Null(await Errado());
        _consultas.Janelas.Remove(_id);
        Assert.Null(await Certo());
        Assert.Empty(_consultas.Usos);
    }

    [Fact]
    public async Task Fora_da_janela_nao_consome_o_throttle()
    {
        _consultas.Janelas.Remove(_id);
        for (var i = 0; i < 30; i++) Assert.Null(await Certo());

        _consultas.Janelas[_id] = _janela;
        Assert.Equal(_janela, await Certo());
    }

    [Fact]
    public async Task O_par_identificador_ip_bloqueia_depois_de_10_falhas_e_o_identificador_sozinho_nao()
    {
        for (var i = 0; i < 10; i++) await Errado();

        Assert.Null(await Certo());              // o par está bloqueado, mesmo com o segredo certo
        Assert.Equal(_janela, await Certo(IpB)); // o mesmo identificador de outro IP entra
        Assert.Contains(_log.Linhas, l => l.Contains("bloqueado"));
    }

    [Fact]
    public async Task O_bloqueio_do_par_vence_em_5_minutos()
    {
        for (var i = 0; i < 10; i++) await Errado();
        _relogio.UtcNow = AuthFixture.Agora.AddMinutes(5).AddSeconds(1);

        Assert.Equal(_janela, await Certo());
    }

    [Fact]
    public async Task Sucesso_zera_o_par()
    {
        for (var i = 0; i < 9; i++) await Errado();
        Assert.Equal(_janela, await Certo());

        for (var i = 0; i < 9; i++) await Errado();
        Assert.Equal(_janela, await Certo());
    }

    [Fact]
    public async Task O_ip_que_varre_identificadores_e_bloqueado_no_limite_mais_alto()
    {
        for (var i = 0; i < ThrottleDoHarbor.LimitePorIp; i++) await Errado(Guid.NewGuid());

        Assert.Null(await Certo());
        Assert.Equal(_janela, await Certo(IpB));
    }

    [Fact]
    public async Task Atras_do_proxy_nao_bloqueia_por_par_nem_por_ip_so_alerta()
    {
        _opcoes.AtrasDoProxy = true;

        for (var i = 0; i < 11; i++) await Errado();
        Assert.Equal(_janela, await Certo());

        for (var i = 0; i < ThrottleDoHarbor.LimitePorIp + 1; i++) await Errado(Guid.NewGuid());
        Assert.Equal(_janela, await Certo());
    }

    [Fact]
    public async Task Falhas_demais_do_mesmo_identificador_geram_alerta_sem_bloquear()
    {
        var alertas = new LogCapturado<ThrottleDoHarbor>();
        var throttle = new ThrottleDoHarbor(_contadores, new IpHasher(new byte[32]), Options.Create(_opcoes), alertas);
        var handler = new AutenticarNoHarborHandler(_consultas, throttle, SemAlerta(), new IpHasher(new byte[32]), _log);

        for (var i = 0; i < ThrottleDoHarbor.AlertaPorIdentificador; i++)
            await handler.Handle(new AutenticarNoHarborCommand(_id.ToString(), SegredoDeTransmissao.Gerar().Segredo, $"192.0.2.{i + 1}"), CancellationToken.None);

        Assert.Contains(alertas.Linhas, l => l.Contains("HARBOR_ALERTA_IDENTIFICADOR") && l.Contains(_id.ToString()));
        Assert.Equal(_janela, await handler.Handle(new AutenticarNoHarborCommand(_id.ToString(), _segredo, "192.0.2.200"), CancellationToken.None));
    }

    [Theory]
    [InlineData("xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("yAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Adulterar_sempre_devolve_outro_valor_do_mesmo_formato(string segredo)
    {
        var outro = Adulterar(segredo);

        Assert.NotEqual(segredo, outro);
        Assert.True(SegredoDeTransmissao.SenhaValida(outro));
    }

    [Fact]
    public async Task Contador_fora_nao_derruba_o_ao_vivo_segue_sem_throttle_e_registra_erro()
    {
        _contadores = new ThrottleQueFalha();
        var alertas = new LogCapturado<ThrottleDoHarbor>();
        var handler = new AutenticarNoHarborHandler(_consultas,
            new ThrottleDoHarbor(_contadores, new IpHasher(new byte[32]), Options.Create(_opcoes), alertas), SemAlerta(), new IpHasher(new byte[32]), _log);

        Assert.Equal(_janela, await handler.Handle(new AutenticarNoHarborCommand(_id.ToString(), _segredo, IpA), CancellationToken.None));
        Assert.Null(await handler.Handle(new AutenticarNoHarborCommand(_id.ToString(), Adulterar(_segredo), IpA), CancellationToken.None));
        Assert.Contains(alertas.Linhas, l => l.Contains("HARBOR_THROTTLE_INDISPONIVEL"));
    }

    [Fact]
    public async Task Log_leva_o_ip_anonimizado_e_nunca_o_segredo()
    {
        await Errado();
        await Certo();
        await Auth(_id.ToString(), _segredo, "2001:db8:1234:5678:9abc:def0:1234:5678");

        var log = string.Join("\n", _log.Linhas);
        Assert.Contains("203.0.113.0/24", log);
        Assert.DoesNotContain(IpA, log);
        Assert.Contains("2001:db8:1234:5678::/64", log);
        Assert.DoesNotContain("9abc", log);
        Assert.DoesNotContain(_segredo, log);
    }

    [Fact]
    public void Comando_nao_imprime_a_senha()
    {
        Assert.DoesNotContain(_segredo, new AutenticarNoHarborCommand(_id.ToString(), _segredo, IpA).ToString());
    }

    [Theory]
    [InlineData("nao-e-guid")]
    [InlineData("3F2B1C4D-5E6F-4A7B-8C9D-0E1F2A3B4C5D")]
    [InlineData("")]
    public async Task Autorizacao_com_identificador_invalido_e_negada_sem_banco(string identificador)
    {
        var r = await new AutorizacaoDoHarborHandler(_consultas, new LogCapturado<AutorizacaoDoHarborHandler>())
            .Handle(new AutorizacaoDoHarborQuery(identificador), CancellationToken.None);

        Assert.Null(r);
        Assert.Equal(0, _consultas.Chamadas);
    }

    [Fact]
    public async Task Autorizacao_segue_a_janela()
    {
        var handler = new AutorizacaoDoHarborHandler(_consultas, new LogCapturado<AutorizacaoDoHarborHandler>());

        Assert.Equal(_janela, await handler.Handle(new AutorizacaoDoHarborQuery(_id.ToString()), CancellationToken.None));
        _consultas.Janelas.Remove(_id);
        Assert.Null(await handler.Handle(new AutorizacaoDoHarborQuery(_id.ToString()), CancellationToken.None));
    }
}
