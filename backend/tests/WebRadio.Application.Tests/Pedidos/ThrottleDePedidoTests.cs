using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Pedidos;
using WebRadio.Domain.Erros;
using Xunit;
using static WebRadio.Application.Tests.Pedidos.PedidosFixture;

namespace WebRadio.Application.Tests.Pedidos;

/// <summary>07 E3-D2 e E3-D10: a ordem das regras, o maior TTL, o teto global e o modo degradado com o Redis fora.</summary>
public class ThrottleDePedidoTests
{
    private readonly PedidosFixture _f = new();

    private static async Task<MuitosPedidosException> Barrado(Task tarefa) => await Assert.ThrowsAsync<MuitosPedidosException>(() => tarefa);

    [Fact]
    public async Task O_primeiro_pedido_passa_e_o_segundo_do_mesmo_dispositivo_leva_429_com_o_TTL_restante()
    {
        var ouvinte = Guid.NewGuid();
        await _f.Throttle.AvaliarAsync(Comando(ouvinte), default);

        _f.Relogio.UtcNow = _f.Relogio.UtcNow.AddSeconds(100);
        var erro = await Barrado(_f.Throttle.AvaliarAsync(Comando(ouvinte), default));

        Assert.Equal(TimeSpan.FromSeconds(200), erro.TenteDeNovoEm);
    }

    [Fact]
    public async Task Depois_de_5_minutos_o_mesmo_dispositivo_pode_pedir_de_novo()
    {
        var ouvinte = Guid.NewGuid();
        await _f.Throttle.AvaliarAsync(Comando(ouvinte), default);

        _f.Relogio.UtcNow = _f.Relogio.UtcNow.AddMinutes(5).AddSeconds(1);

        await _f.Throttle.AvaliarAsync(Comando(ouvinte), default);
    }

    [Fact]
    public async Task O_quarto_pedido_de_dispositivos_diferentes_no_mesmo_IP_leva_429_e_outro_IP_passa()
    {
        for (var i = 0; i < 3; i++) await _f.Throttle.AvaliarAsync(Comando(ip: "203.0.113.7"), default);

        await Barrado(_f.Throttle.AvaliarAsync(Comando(ip: "203.0.113.7"), default));
        await _f.Throttle.AvaliarAsync(Comando(ip: "198.51.100.9"), default);
    }

    [Fact]
    public async Task O_limite_por_IP_e_configuravel()
    {
        var f = new PedidosFixture(new() { LimitePorIp = 1 });
        await f.Throttle.AvaliarAsync(Comando(ip: "203.0.113.7"), default);

        await Barrado(f.Throttle.AvaliarAsync(Comando(ip: "203.0.113.7"), default));
    }

    [Fact]
    public async Task IPv6_do_mesmo_64_conta_como_o_mesmo_IP()
    {
        for (var i = 0; i < 3; i++) await _f.Throttle.AvaliarAsync(Comando(ip: $"2001:db8:1:2::{i + 1}"), default);

        await Barrado(_f.Throttle.AvaliarAsync(Comando(ip: "2001:db8:1:2:ffff::9"), default));
    }

    [Fact]
    public async Task Pedido_barrado_pelo_dispositivo_nao_gasta_a_cota_do_IP()
    {
        var ouvinte = Guid.NewGuid();
        await _f.Throttle.AvaliarAsync(Comando(ouvinte, ip: "203.0.113.7"), default);
        for (var i = 0; i < 5; i++) await Barrado(_f.Throttle.AvaliarAsync(Comando(ouvinte, ip: "203.0.113.7"), default));

        // Só o primeiro contou no IP (1 de 3): mais dois dispositivos passam.
        await _f.Throttle.AvaliarAsync(Comando(ip: "203.0.113.7"), default);
        await _f.Throttle.AvaliarAsync(Comando(ip: "203.0.113.7"), default);
        await Barrado(_f.Throttle.AvaliarAsync(Comando(ip: "203.0.113.7"), default));
    }

    [Fact]
    public async Task Usuario_logado_tem_a_propria_vaga_e_o_Retry_After_e_o_maior_TTL()
    {
        var usuario = Guid.NewGuid();
        await _f.Throttle.AvaliarAsync(Comando(usuarioId: usuario), default); // reserva dispositivo A e usuário

        _f.Relogio.UtcNow = _f.Relogio.UtcNow.AddSeconds(60);
        var outroDispositivo = Guid.NewGuid();
        var soUsuario = await Barrado(_f.Throttle.AvaliarAsync(Comando(outroDispositivo, usuarioId: usuario), default));
        Assert.Equal(TimeSpan.FromSeconds(240), soUsuario.TenteDeNovoEm); // dispositivo B é novo; o usuário foi há 60 s

        // Dispositivo B foi reservado antes do 429 do usuário (E3-D2): agora os dois bloqueiam, e vale o MAIOR TTL.
        _f.Relogio.UtcNow = _f.Relogio.UtcNow.AddSeconds(30);
        var ambos = await Barrado(_f.Throttle.AvaliarAsync(Comando(outroDispositivo, usuarioId: usuario), default));
        Assert.Equal(TimeSpan.FromSeconds(270), ambos.TenteDeNovoEm); // dispositivo B (reservado há 30 s) pesa mais que o usuário (há 90 s)
    }

    [Fact]
    public async Task Teto_global_barra_antes_de_reservar_o_dispositivo_e_responde_60_segundos()
    {
        _f.Consultas.TetoAtingido = true;
        var ouvinte = Guid.NewGuid();

        var erro = await Barrado(_f.Throttle.AvaliarAsync(Comando(ouvinte), default));

        Assert.Equal(TimeSpan.FromSeconds(60), erro.TenteDeNovoEm);
        Assert.Equal(0, _f.Redis.Chamadas); // nem chegou ao Redis: o dispositivo não fica com a reserva de 5 min

        _f.Consultas.TetoAtingido = false;
        _f.Relogio.UtcNow = _f.Relogio.UtcNow.AddSeconds(6); // o cache do teto vale 5 s
        await _f.Throttle.AvaliarAsync(Comando(ouvinte), default);
    }

    [Fact]
    public async Task A_contagem_do_teto_fica_em_cache_por_5_segundos()
    {
        for (var i = 0; i < 3; i++) await _f.Throttle.AvaliarAsync(Comando(ip: $"203.0.113.{i + 10}"), default);
        Assert.Equal(1, _f.Consultas.ConsultasDoTeto);

        _f.Relogio.UtcNow = _f.Relogio.UtcNow.AddSeconds(6);
        await _f.Throttle.AvaliarAsync(Comando(ip: "203.0.113.50"), default);
        Assert.Equal(2, _f.Consultas.ConsultasDoTeto);
    }

    [Fact]
    public async Task Redis_fora_sinaliza_o_fallback_SQL_e_abre_o_disjuntor_sem_pagar_o_timeout_de_novo()
    {
        _f.Redis.Fora = true;

        await _f.Throttle.AvaliarAsync(Comando(), default);

        Assert.True(_f.Estado.UsarFallbackSql);
        var chamadas = _f.Redis.Chamadas;

        var outro = new EstadoDoThrottle();
        await new ThrottleDePedido(_f.Redis, _f.Consultas, _f.Identidade, _f.Disjuntor, _f.Cache, outro,
            Microsoft.Extensions.Options.Options.Create(_f.Opcoes), Microsoft.Extensions.Logging.Abstractions.NullLogger<ThrottleDePedido>.Instance)
            .AvaliarAsync(Comando(), default);

        Assert.True(outro.UsarFallbackSql);
        Assert.Equal(chamadas, _f.Redis.Chamadas); // disjuntor aberto: nenhuma chamada nova ao Redis
    }

    [Fact]
    public async Task Depois_dos_30_segundos_uma_sonda_testa_o_Redis_e_fecha_o_disjuntor_se_ele_voltou()
    {
        _f.Redis.Fora = true;
        await _f.Throttle.AvaliarAsync(Comando(), default);
        _f.Redis.Fora = false;
        _f.Relogio.UtcNow = _f.Relogio.UtcNow.AddSeconds(31);

        var estado = new EstadoDoThrottle();
        await new ThrottleDePedido(_f.Redis, _f.Consultas, _f.Identidade, _f.Disjuntor, _f.Cache, estado,
            Microsoft.Extensions.Options.Options.Create(_f.Opcoes), Microsoft.Extensions.Logging.Abstractions.NullLogger<ThrottleDePedido>.Instance)
            .AvaliarAsync(Comando(), default);

        Assert.False(estado.UsarFallbackSql);
        Assert.True(_f.Disjuntor.Fechado);
    }

    [Fact]
    public async Task Sonda_que_falha_reabre_o_disjuntor_por_mais_30_segundos()
    {
        _f.Redis.Fora = true;
        await _f.Throttle.AvaliarAsync(Comando(), default);
        _f.Relogio.UtcNow = _f.Relogio.UtcNow.AddSeconds(31);

        await _f.Throttle.AvaliarAsync(Comando(), default); // a sonda falha
        var chamadas = _f.Redis.Chamadas;
        _f.Relogio.UtcNow = _f.Relogio.UtcNow.AddSeconds(10);
        await _f.Throttle.AvaliarAsync(Comando(), default);

        Assert.Equal(chamadas, _f.Redis.Chamadas);
        Assert.False(_f.Disjuntor.Fechado);
    }

    [Fact]
    public async Task O_429_do_Redis_nao_abre_o_disjuntor()
    {
        var ouvinte = Guid.NewGuid();
        await _f.Throttle.AvaliarAsync(Comando(ouvinte), default);
        await Barrado(_f.Throttle.AvaliarAsync(Comando(ouvinte), default));

        Assert.True(_f.Disjuntor.Fechado);
    }

    [Fact]
    public async Task As_chaves_do_Redis_levem_so_hash_nunca_o_listenerId_nem_o_IP()
    {
        var ouvinte = Guid.NewGuid();
        var chaves = new List<string>();
        var espiao = new Espiao(_f.Redis, chaves);
        var estado = new EstadoDoThrottle();
        await new ThrottleDePedido(espiao, _f.Consultas, _f.Identidade, _f.Disjuntor, _f.Cache, estado,
            Microsoft.Extensions.Options.Options.Create(_f.Opcoes), Microsoft.Extensions.Logging.Abstractions.NullLogger<ThrottleDePedido>.Instance)
            .AvaliarAsync(Comando(ouvinte, ip: "203.0.113.7", usuarioId: Guid.NewGuid()), default);

        Assert.Equal(3, chaves.Count);
        Assert.All(chaves, c =>
        {
            Assert.StartsWith("throttle:pedido:", c);
            Assert.DoesNotContain(ouvinte.ToString("N"), c, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ouvinte.ToString("D"), c, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("203.0.113", c);
        });
    }

    private sealed class Espiao(IRequestThrottle interno, List<string> chaves) : IRequestThrottle
    {
        public Task<bool> TentarReservarAsync(string chave, TimeSpan ttl, CancellationToken ct = default) { chaves.Add(chave); return interno.TentarReservarAsync(chave, ttl, ct); }

        public Task<long> IncrementarAsync(string chave, TimeSpan ttl, CancellationToken ct = default) { chaves.Add(chave); return interno.IncrementarAsync(chave, ttl, ct); }

        public Task<long> LerAsync(string chave, CancellationToken ct = default) => interno.LerAsync(chave, ct);

        public Task RemoverAsync(string chave, CancellationToken ct = default) => interno.RemoverAsync(chave, ct);

        public Task<TimeSpan?> TempoRestanteAsync(string chave, CancellationToken ct = default) => interno.TempoRestanteAsync(chave, ct);
    }
}
