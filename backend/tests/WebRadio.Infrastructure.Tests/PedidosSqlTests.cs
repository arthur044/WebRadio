using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Pedidos;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Grade;
using WebRadio.Domain.Interacao;
using WebRadio.Infrastructure.Pedidos;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using WebRadio.Infrastructure.Seguranca;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// E3-F01 a F03 contra SQL Server de verdade: o programa da janela com o relógio do SQL, o teto global, o fallback do throttle sob
/// <c>sp_getapplock</c>, a moderação concorrente (RowVersion) e o texto das consultas (constantes, para o índice filtrado).
/// </summary>
public class PedidosSqlTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private readonly Cenario _c = new(banco);

    private static DateTime Agora() => DateTime.UtcNow;

    private ConsultasDePedidos Consultas() => new(_c.NovoDb());

    private ListenerIdentity Identidade() => new(_c.Pepper, new IpHasher(_c.Pepper));

    /// <summary>O banco é compartilhado pela classe e a janela é global: cada teste começa sem pedidos nem programas.</summary>
    private async Task LimparAsync()
    {
        await using var db = _c.NovoDb();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM interacao.PedidoMusica; DELETE FROM grade.Programa; DELETE FROM infra.EventoOutbox;");
    }

    private async Task<Programa> ProgramaAsync(TimeSpan inicio, TimeSpan duracao, Action<Programa, DateTime>? status = null)
    {
        var locutor = await _c.CriarUsuarioAsync(Cenario.Email(), Role.Locutor);
        var ini = Truncar(Agora().Add(inicio));
        var programa = new Programa(Guid.NewGuid(), "Programa", null, locutor.Id, ini, ini.Add(duracao), ini);
        status?.Invoke(programa, ini.AddSeconds(1));
        await using var db = _c.NovoDb();
        db.Programas.Add(programa);
        await db.SaveChangesAsync();
        return programa;
    }

    private static DateTime Truncar(DateTime d) => new(d.Ticks - (d.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    private async Task<PedidoMusica> InserirAsync(Guid? listenerId = null, byte[]? ipHash = null, Guid? usuarioId = null, TimeSpan? idade = null,
        StatusPedido status = StatusPedido.Pendente, DateTime? moderadoEm = null)
    {
        var criado = Agora() - (idade ?? TimeSpan.Zero);
        var p = new PedidoMusica(Guid.NewGuid(), null, usuarioId, "Maria", "Música", "Artista", null,
            Identidade().DeviceHash(listenerId ?? Guid.NewGuid()), ipHash, criado);
        if (status == StatusPedido.Aprovado)
        {
            var moderador = usuarioId ?? (await _c.CriarUsuarioAsync(Cenario.Email())).Id;
            p.Aprovar(moderador, null, moderadoEm ?? criado);
        }

        await using var db = _c.NovoDb();
        db.PedidosMusica.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    // ---- E3-D1: ProgramaId da janela ----

    [SqlFact]
    public async Task Programa_da_janela_vale_com_o_Status_Agendado_o_Worker_nao_e_exigido()
    {
        await LimparAsync();
        var programa = await ProgramaAsync(TimeSpan.FromMinutes(-10), TimeSpan.FromHours(1));

        Assert.Equal(programa.Id, await Consultas().ProgramaDaJanelaAsync(default));
    }

    [SqlFact]
    public async Task Programa_AoVivo_cuja_janela_passou_com_o_Worker_parado_nao_conta()
    {
        await LimparAsync();
        await ProgramaAsync(TimeSpan.FromHours(-3), TimeSpan.FromHours(1), (p, t) => p.IniciarAoVivo(t));

        Assert.Null(await Consultas().ProgramaDaJanelaAsync(default));
    }

    [SqlFact]
    public async Task Programa_futuro_finalizado_ou_cancelado_nao_e_o_da_janela()
    {
        await LimparAsync();
        await ProgramaAsync(TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        await ProgramaAsync(TimeSpan.FromMinutes(-10), TimeSpan.FromHours(1), (p, t) => p.Cancelar(t));

        Assert.Null(await Consultas().ProgramaDaJanelaAsync(default));
    }

    [SqlFact]
    public async Task Programa_de_12_horas_que_comecou_ha_11_horas_ainda_e_encontrado()
    {
        await LimparAsync();
        var programa = await ProgramaAsync(TimeSpan.FromHours(-11), TimeSpan.FromHours(12));

        Assert.Equal(programa.Id, await Consultas().ProgramaDaJanelaAsync(default));
    }

    // ---- E3-D10: teto global ----

    [SqlFact]
    public async Task Teto_conta_so_os_Pendente_e_para_de_ler_no_teto()
    {
        await LimparAsync();
        for (var i = 0; i < 3; i++) await InserirAsync();
        await InserirAsync(status: StatusPedido.Aprovado);

        Assert.True(await Consultas().TetoDePendentesAtingidoAsync(3, default));
        Assert.False(await Consultas().TetoDePendentesAtingidoAsync(4, default));
    }

    [SqlFact]
    public async Task DoD_6_com_o_teto_atingido_o_proximo_dispositivo_leva_429_sem_ficar_com_a_reserva_de_5_minutos()
    {
        await LimparAsync();
        var opcoes = new PedidosOptions { TetoPendentes = 3 };
        for (var i = 0; i < 3; i++) await InserirAsync();
        var redis = new MemoryRequestThrottle(_c.Relogio);
        var ouvinte = Guid.NewGuid();

        ThrottleDePedido Novo(PedidosOptions o) => new(redis, Consultas(), Identidade(), new IpHasher(_c.Pepper), new DisjuntorDoRedis(_c.Relogio), new CacheDoTeto(_c.Relogio),
            new EstadoDoThrottle(), Options.Create(o), NullLogger<ThrottleDePedido>.Instance);
        var comando = new CriarPedidoCommand(ouvinte, IPAddress.Parse("203.0.113.7"), null, "Maria", "Música", "Artista", null);

        var erro = await Assert.ThrowsAsync<MuitosPedidosException>(() => Novo(opcoes).AvaliarAsync(comando, default));
        Assert.Equal(TimeSpan.FromSeconds(60), erro.TenteDeNovoEm);

        // Teto folgado: o mesmo dispositivo passa de imediato, ou seja, o 429 do teto não deixou reserva no Redis.
        await Novo(new PedidosOptions { TetoPendentes = 300 }).AvaliarAsync(comando, default);
    }

    // ---- E3-F01: fallback SQL ----

    [SqlFact]
    public async Task Fallback_dispositivo_e_usuario_esperam_5_minutos_menos_a_idade_do_pedido_mais_recente()
    {
        await LimparAsync();
        var ouvinte = Guid.NewGuid();
        var usuario = (await _c.CriarUsuarioAsync(Cenario.Email())).Id;
        await InserirAsync(ouvinte, usuarioId: usuario, idade: TimeSpan.FromSeconds(100));
        await InserirAsync(ouvinte, usuarioId: usuario, idade: TimeSpan.FromSeconds(200));
        var identidade = Identidade();

        var espera = await Consultas().EsperaDoFallbackAsync(identidade.DeviceHash(ouvinte), new byte[32], usuario, 3, default);

        Assert.InRange(espera.Dispositivo!.Value.TotalSeconds, 195, 201); // 300 − 100 (com margem do relógio)
        Assert.InRange(espera.Usuario!.Value.TotalSeconds, 195, 201);
        Assert.Null(espera.Ip);
    }

    [SqlFact]
    public async Task Fallback_ignora_pedidos_com_mais_de_5_minutos_e_usuario_nulo_nao_consulta_usuario()
    {
        await LimparAsync();
        var ouvinte = Guid.NewGuid();
        await InserirAsync(ouvinte, idade: TimeSpan.FromMinutes(6));

        var espera = await Consultas().EsperaDoFallbackAsync(Identidade().DeviceHash(ouvinte), new byte[32], null, 3, default);

        Assert.Equal(EsperaDoThrottle.Livre, espera);
    }

    [SqlFact]
    public async Task Fallback_por_IP_so_bloqueia_com_N_pedidos_e_espera_o_mais_antigo_dos_N_sair()
    {
        await LimparAsync();
        var ip = Identidade().IpHash(IPAddress.Parse("203.0.113.7"));
        await InserirAsync(ipHash: ip, idade: TimeSpan.FromSeconds(250)); // o mais antigo dos três
        await InserirAsync(ipHash: ip, idade: TimeSpan.FromSeconds(100));
        await InserirAsync(ipHash: Identidade().IpHash(IPAddress.Parse("198.51.100.1")), idade: TimeSpan.FromSeconds(10)); // outro IP

        Assert.Null((await Consultas().EsperaDoFallbackAsync(new byte[32], ip, null, 3, default)).Ip); // só 2 no IP

        await InserirAsync(ipHash: ip, idade: TimeSpan.FromSeconds(50));
        var espera = await Consultas().EsperaDoFallbackAsync(new byte[32], ip, null, 3, default);

        Assert.InRange(espera.Ip!.Value.TotalSeconds, 49, 55); // 300 − 250
    }

    [SqlFact]
    public async Task Fallback_o_pedido_na_borda_da_janela_nunca_gera_Retry_After_zero()
    {
        await LimparAsync();
        var ouvinte = Guid.NewGuid();
        await InserirAsync(ouvinte, idade: TimeSpan.FromSeconds(299.9));

        var espera = await Consultas().EsperaDoFallbackAsync(Identidade().DeviceHash(ouvinte), new byte[32], null, 3, default);

        Assert.True(espera.Dispositivo is { } d ? d >= TimeSpan.FromSeconds(1) : true);
    }

    private CriarPedidoHandler HandlerDeFallback(out EstadoDoThrottle estado, int limitePorIp = 3)
    {
        var db = _c.NovoDb();
        estado = new EstadoDoThrottle { UsarFallbackSql = true };
        return new CriarPedidoHandler(db, new TransacaoComTrava(db), new ConsultasDePedidos(db), Identidade(), new IpHasher(_c.Pepper), new OutboxEf(db), _c.Relogio, estado,
            Options.Create(new PedidosOptions { LimitePorIp = limitePorIp }));
    }

    [SqlFact]
    public async Task Fallback_dois_POST_simultaneos_do_mesmo_dispositivo_um_201_e_um_429()
    {
        await LimparAsync();
        var ouvinte = Guid.NewGuid();
        var comando = new CriarPedidoCommand(ouvinte, IPAddress.Parse("203.0.113.7"), null, "Maria", "Música", "Artista", null);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => TentarAsync(comando)));

        Assert.Equal(1, resultados.Count(r => r));
        Assert.Equal(1, resultados.Count(r => !r));
        await using var db = _c.NovoDb();
        Assert.Equal(1, await db.PedidosMusica.CountAsync());
        Assert.Equal(1, await db.EventosOutbox.CountAsync(e => e.Tipo == "PedidoCriado"));
    }

    private async Task<bool> TentarAsync(CriarPedidoCommand comando, int limitePorIp = 3)
    {
        try
        {
            await HandlerDeFallback(out _, limitePorIp).Handle(comando, default);
            return true;
        }
        catch (MuitosPedidosException)
        {
            return false;
        }
    }

    [SqlFact]
    public async Task Fallback_rajada_do_mesmo_IP_com_listenerIds_diferentes_respeita_o_limite_por_IP()
    {
        await LimparAsync();
        var ip = IPAddress.Parse("203.0.113.9");

        // Limite de 2 por IP e 8 requisições simultâneas, cada uma de um dispositivo diferente: sem a trava do IP todas leriam 0 e passariam.
        var resultados = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => TentarAsync(new CriarPedidoCommand(Guid.NewGuid(), ip, null, "Maria", "Música", "Artista", null), limitePorIp: 2)));

        Assert.Equal(2, resultados.Count(r => r));
        await using var db = _c.NovoDb();
        Assert.Equal(2, await db.PedidosMusica.CountAsync());
    }

    [SqlFact]
    public async Task Fallback_rajada_do_mesmo_usuario_com_dispositivos_e_IPs_diferentes_so_deixa_passar_um()
    {
        await LimparAsync();
        var usuario = (await _c.CriarUsuarioAsync(Cenario.Email())).Id;

        var resultados = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(i => TentarAsync(new CriarPedidoCommand(Guid.NewGuid(), IPAddress.Parse($"203.0.113.{100 + i}"), usuario, "Maria", "Música", "Artista", null))));

        Assert.Equal(1, resultados.Count(r => r));
    }

    [SqlFact]
    public async Task Fallback_grava_o_pedido_com_o_programa_da_janela_do_SQL()
    {
        await LimparAsync();
        var programa = await ProgramaAsync(TimeSpan.FromMinutes(-5), TimeSpan.FromHours(1));

        var dto = await HandlerDeFallback(out _).Handle(new CriarPedidoCommand(Guid.NewGuid(), IPAddress.Parse("203.0.113.7"), null, "Maria", "Música", "Artista", "oi"), default);

        Assert.Equal(programa.Id, dto.ProgramaId);
        await using var db = _c.NovoDb();
        var p = await db.PedidosMusica.SingleAsync();
        Assert.Equal(programa.Id, p.ProgramaId);
        Assert.NotNull(p.ListenerDeviceHash);
        Assert.NotNull(p.ListenerIpHash);
    }

    [SqlFact]
    public async Task Texto_com_emoji_ZWJ_e_NFC_vai_e_volta_do_nvarchar_sem_perda()
    {
        await LimparAsync();
        const string familia = "\U0001F468‍\U0001F469‍\U0001F467";

        var dto = await HandlerDeFallback(out _).Handle(new CriarPedidoCommand(Guid.NewGuid(), IPAddress.Parse("203.0.113.7"), null, "José " + familia, "Música", "Artista", null), default);

        await using var db = _c.NovoDb();
        Assert.Equal("José " + familia, (await db.PedidosMusica.SingleAsync(p => p.Id == dto.Id)).NomeOuvinte);
    }

    // ---- E3-F03: moderação concorrente ----

    [SqlFact]
    public async Task DoD_4_oito_locutores_aprovando_o_mesmo_pedido_ao_mesmo_tempo_um_200_e_o_resto_409()
    {
        await LimparAsync();
        var pedido = await InserirAsync();
        var moderadores = new List<Guid>();
        for (var i = 0; i < 8; i++) moderadores.Add((await _c.CriarUsuarioAsync(Cenario.Email())).Id);

        var resultados = await Task.WhenAll(moderadores.Select(async m =>
        {
            var db = _c.NovoDb();
            try
            {
                await new AprovarPedidoHandler(db, new OutboxEf(db), _c.Relogio).Handle(new AprovarPedidoCommand(pedido.Id, m, null), default);
                return true;
            }
            catch (PedidoJaModeradoException)
            {
                return false;
            }
        }));

        Assert.Equal(1, resultados.Count(r => r));
        Assert.Equal(7, resultados.Count(r => !r));
        await using var conferir = _c.NovoDb();
        Assert.Equal(1, await conferir.EventosOutbox.CountAsync(e => e.Tipo == "PedidoModerado"));
        Assert.Equal(StatusPedido.Aprovado, (await conferir.PedidosMusica.SingleAsync()).Status);
    }

    [SqlFact]
    public async Task Rejeitar_e_aprovar_ao_mesmo_tempo_deixa_so_um_vencedor()
    {
        await LimparAsync();
        var pedido = await InserirAsync();
        var m1 = (await _c.CriarUsuarioAsync(Cenario.Email())).Id;
        var m2 = (await _c.CriarUsuarioAsync(Cenario.Email())).Id;

        async Task<bool> Tentar(Func<RadioDbContext, Task> acao)
        {
            var db = _c.NovoDb();
            try
            {
                await acao(db);
                return true;
            }
            catch (PedidoJaModeradoException)
            {
                return false;
            }
        }

        var r = await Task.WhenAll(
            Tentar(db => new AprovarPedidoHandler(db, new OutboxEf(db), _c.Relogio).Handle(new AprovarPedidoCommand(pedido.Id, m1, null), default)),
            Tentar(db => new RejeitarPedidoHandler(db, new OutboxEf(db), _c.Relogio).Handle(new RejeitarPedidoCommand(pedido.Id, m2, "Fora do estilo"), default)));

        Assert.Equal(1, r.Count(x => x));
    }

    // ---- E3-F02: texto SQL das consultas ----

    [SqlFact]
    public async Task A_fila_de_pendentes_compara_Status_com_constante_ordena_e_projeta_so_o_INCLUDE_do_indice()
    {
        await using var db = _c.NovoDb();

        var sql = ListarPedidosHandler.FilaDePendentes(db).OrderBy(p => p.CriadoEmUtc).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.NomeOuvinte, p.TituloMusica, p.Artista, p.Mensagem, p.CriadoEmUtc, p.ProgramaId })
            .Skip(0).Take(20).ToQueryString();

        Assert.Matches(@"\[p\]\.\[Status\] = CAST\(1 AS tinyint\)|\[p\]\.\[Status\] = 1", sql);
        Assert.DoesNotMatch(@"Status\] = @", sql);
        Assert.Contains("ORDER BY [p].[CriadoEmUtc], [p].[Id]", sql);
        Assert.DoesNotContain("RowVersion", sql);
        Assert.DoesNotContain("UsuarioId", sql);
        Assert.DoesNotContain("Hash", sql);
    }

    [SqlFact]
    public async Task A_lista_de_aprovados_usa_Status_constante_e_o_corte_em_ModeradoEmUtc()
    {
        await using var db = _c.NovoDb();

        var sql = ListarPedidosHandler.ListaDeAprovados(db, Agora().AddHours(-24)).OrderBy(p => p.ModeradoEmUtc).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.NomeOuvinte, p.ModeradoEmUtc }).ToQueryString();

        Assert.Matches(@"\[p\]\.\[Status\] = CAST\(2 AS tinyint\)|\[p\]\.\[Status\] = 2", sql);
        Assert.DoesNotMatch(@"Status\] = @", sql);
        Assert.Contains("[p].[ModeradoEmUtc] >=", sql);
        Assert.Contains("ORDER BY [p].[ModeradoEmUtc], [p].[Id]", sql);
    }

    [SqlFact]
    public async Task A_fila_e_a_lista_de_aprovados_rodam_no_SQL_de_verdade_com_a_ordem_certa()
    {
        await LimparAsync();
        await InserirAsync(idade: TimeSpan.FromMinutes(5));
        await InserirAsync(idade: TimeSpan.FromMinutes(50));
        var moderador = (await _c.CriarUsuarioAsync(Cenario.Email())).Id;
        await InserirAsync(status: StatusPedido.Aprovado, usuarioId: moderador, moderadoEm: Agora().AddHours(-30));
        await InserirAsync(status: StatusPedido.Aprovado, usuarioId: moderador, moderadoEm: Agora().AddHours(-1));

        await using var db = _c.NovoDb();
        var fila = await new ListarPedidosHandler(db, _c.Relogio).Handle(new ListarPedidosQuery(null, 1, 20), default);
        var aprovados = await new ListarPedidosHandler(db, new RelogioDeAgora()).Handle(new ListarPedidosQuery("Aprovado", 1, 20), default);

        Assert.Equal(2, fila.Total);
        Assert.True(fila.Itens[0].CriadoEmUtc < fila.Itens[1].CriadoEmUtc);
        Assert.Equal(1, aprovados.Total);
    }

    private sealed class RelogioDeAgora : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
