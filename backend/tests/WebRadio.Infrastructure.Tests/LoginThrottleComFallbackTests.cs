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
}
