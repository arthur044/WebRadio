using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Seguranca;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

public class LoginThrottleComFallbackTests
{
    private sealed class ThrottleContador(bool quebrado) : ILoginThrottle
    {
        public int Chamadas { get; private set; }

        public bool Bloqueado { get; set; }

        private Task Contar()
        {
            Chamadas++;
            return quebrado ? throw new InvalidOperationException("Redis fora do ar (simulado)") : Task.CompletedTask;
        }

        public async Task<AvaliacaoDeLogin> AvaliarAsync(string e, byte[] i, CancellationToken ct) { await Contar(); return new AvaliacaoDeLogin(Bloqueado, TimeSpan.Zero); }

        public Task RegistrarFalhaAsync(string e, byte[] i, CancellationToken ct) => Contar();

        public Task LimparAsync(string e, byte[] i, CancellationToken ct) => Contar();

        public int Desbloqueios { get; private set; }

        public Task DesbloquearContaAsync(string e, CancellationToken ct)
        {
            Desbloqueios++;
            return quebrado ? throw new InvalidOperationException("Redis fora do ar (simulado)") : Task.CompletedTask;
        }
    }

    private static readonly byte[] Ip = new byte[32];

    [Fact]
    public async Task Redis_saudavel_e_usado_e_a_memoria_nao_e_tocada()
    {
        var redis = new ThrottleContador(quebrado: false) { Bloqueado = true };
        var memoria = new ThrottleContador(quebrado: false);
        var t = new LoginThrottleComFallback(redis, memoria, new RelogioFalso(DateTime.UtcNow), NullLogger<LoginThrottleComFallback>.Instance);

        Assert.True((await t.AvaliarAsync("A@B", Ip, default)).Bloqueado);
        Assert.Equal(0, memoria.Chamadas);
    }

    [Fact]
    public async Task Redis_fora_do_ar_cai_para_a_memoria_e_o_disjuntor_evita_pagar_o_timeout_de_novo()
    {
        var redis = new ThrottleContador(quebrado: true);
        var memoria = new ThrottleContador(quebrado: false);
        var relogio = new RelogioFalso(DateTime.UtcNow);
        var t = new LoginThrottleComFallback(redis, memoria, relogio, NullLogger<LoginThrottleComFallback>.Instance);

        await t.AvaliarAsync("A@B", Ip, default);
        await t.RegistrarFalhaAsync("A@B", Ip, default);
        await t.LimparAsync("A@B", Ip, default);

        Assert.Equal(1, redis.Chamadas);   // só a que descobriu a queda
        Assert.Equal(3, memoria.Chamadas); // as 3 operações foram atendidas pela memória
        Assert.False(t.RedisEmUso);
    }

    [Fact]
    public async Task Depois_de_30s_o_Redis_e_tentado_de_novo()
    {
        var redis = new ThrottleContador(quebrado: true);
        var relogio = new RelogioFalso(DateTime.UtcNow);
        var t = new LoginThrottleComFallback(redis, new ThrottleContador(false), relogio, NullLogger<LoginThrottleComFallback>.Instance);

        await t.AvaliarAsync("A@B", Ip, default);
        relogio.Avancar(LoginThrottleComFallback.TempoAberto + TimeSpan.FromSeconds(1));
        Assert.True(t.RedisEmUso);

        await t.AvaliarAsync("A@B", Ip, default);
        Assert.Equal(2, redis.Chamadas);
    }

    /// <summary>Redis que só responde depois de liberado, para segurar a sonda do meio-aberto.</summary>
    private sealed class RedisSondavel : ILoginThrottle
    {
        private int _chamadas;
        private readonly TaskCompletionSource _liberar = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Chamadas => Volatile.Read(ref _chamadas);
        public bool Quebrado { get; set; } = true;
        public bool Segurar { get; set; }

        public void Liberar() => _liberar.TrySetResult();

        public async Task<AvaliacaoDeLogin> AvaliarAsync(string e, byte[] i, CancellationToken ct)
        {
            Interlocked.Increment(ref _chamadas);
            if (Segurar) await _liberar.Task.WaitAsync(ct);
            return Quebrado ? throw new InvalidOperationException("Redis fora do ar (simulado)") : new AvaliacaoDeLogin(false, TimeSpan.Zero);
        }

        public Task RegistrarFalhaAsync(string e, byte[] i, CancellationToken ct) => Task.CompletedTask;
        public Task LimparAsync(string e, byte[] i, CancellationToken ct) => Task.CompletedTask;
        public Task DesbloquearContaAsync(string e, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Meio_aberto_so_uma_chamada_sonda_o_Redis_e_as_outras_seguem_na_memoria()
    {
        var redis = new RedisSondavel();
        var memoria = new ThrottleContador(quebrado: false);
        var relogio = new RelogioFalso(DateTime.UtcNow);
        var t = new LoginThrottleComFallback(redis, memoria, relogio, NullLogger<LoginThrottleComFallback>.Instance);
        await t.AvaliarAsync("A@B", Ip, default); // abre o disjuntor (a 1ª chamada falha)
        relogio.Avancar(LoginThrottleComFallback.TempoAberto + TimeSpan.FromSeconds(1));
        redis.Quebrado = false;
        redis.Segurar = true;

        var sonda = t.AvaliarAsync("A@B", Ip, default);
        Assert.True(await Espera.Ate(() => redis.Chamadas == 2, TimeSpan.FromSeconds(10)));
        // Com a sonda em voo, as demais NÃO vão ao Redis (sem isto, uma rajada pagaria o timeout de uma vez só).
        for (var i = 0; i < 20; i++) await t.AvaliarAsync("A@B", Ip, default).WaitAsync(TimeSpan.FromSeconds(5)); // sem a sonda única, travaria aqui
        Assert.Equal(2, redis.Chamadas);

        redis.Liberar();
        await sonda;
        await t.AvaliarAsync("A@B", Ip, default); // sonda deu certo: disjuntor fechado, volta ao Redis
        Assert.Equal(3, redis.Chamadas);
    }

    [Fact]
    public async Task Sonda_que_falha_reabre_o_disjuntor_por_mais_30s()
    {
        var redis = new RedisSondavel();
        var relogio = new RelogioFalso(DateTime.UtcNow);
        var t = new LoginThrottleComFallback(redis, new ThrottleContador(false), relogio, NullLogger<LoginThrottleComFallback>.Instance);
        await t.AvaliarAsync("A@B", Ip, default);
        relogio.Avancar(LoginThrottleComFallback.TempoAberto + TimeSpan.FromSeconds(1));

        await t.AvaliarAsync("A@B", Ip, default); // a sonda falha (Redis ainda quebrado)
        Assert.False(t.RedisEmUso);
        await t.AvaliarAsync("A@B", Ip, default);
        Assert.Equal(2, redis.Chamadas); // a 3ª chamada nem tentou o Redis
    }

    [Fact]
    public async Task O_log_do_disjuntor_traz_o_tipo_da_excecao_e_nao_a_mensagem()
    {
        var log = new LoggerDeTeste<LoginThrottleComFallback>();
        var t = new LoginThrottleComFallback(new ThrottleContador(quebrado: true), new ThrottleContador(false), new RelogioFalso(DateTime.UtcNow), log);

        await t.AvaliarAsync("A@B", Ip, default);

        var aviso = Assert.Single(log.Linhas, l => l.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains("InvalidOperationException", aviso.Texto);
        Assert.DoesNotContain("Redis fora do ar (simulado)", aviso.Texto); // a mensagem da exceção pode trazer o endereço do Redis
    }
}
