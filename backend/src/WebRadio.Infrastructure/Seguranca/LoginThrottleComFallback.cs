using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// S-A12: Redis fora do ar não derruba o login. Na primeira falha o disjuntor abre por 30 s e as chamadas vão direto
/// para o throttle em memória (sem pagar o timeout do Redis a cada login); depois tenta o Redis de novo. O evento fica no log.
/// </summary>
public sealed class LoginThrottleComFallback(ILoginThrottle redis, ILoginThrottle memoria, IClock clock, ILogger<LoginThrottleComFallback> logger) : ILoginThrottle
{
    public static readonly TimeSpan TempoAberto = TimeSpan.FromSeconds(30);

    private long _abertoAteTicks;

    public bool RedisEmUso => clock.UtcNow.Ticks >= Volatile.Read(ref _abertoAteTicks);

    public Task<AvaliacaoDeLogin> AvaliarAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct) =>
        Executar(t => t.AvaliarAsync(emailNormalizado, ipHash, ct));

    public Task RegistrarFalhaAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct) =>
        Executar(async t => { await t.RegistrarFalhaAsync(emailNormalizado, ipHash, ct); return 0; });

    public Task LimparAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct) =>
        Executar(async t => { await t.LimparAsync(emailNormalizado, ipHash, ct); return 0; });

    /// <summary>
    /// Desbloqueia nos DOIS: a memória pode ter falhas contadas durante uma queda do Redis, e o Admin espera que a conta
    /// fique livre de qualquer forma. Falha do Redis aqui propaga (o Admin vê o erro e repete), sem abrir o disjuntor.
    /// </summary>
    public async Task DesbloquearContaAsync(string emailNormalizado, CancellationToken ct)
    {
        await memoria.DesbloquearContaAsync(emailNormalizado, ct);
        await redis.DesbloquearContaAsync(emailNormalizado, ct);
    }

    private async Task<T> Executar<T>(Func<ILoginThrottle, Task<T>> operacao)
    {
        if (!RedisEmUso) return await operacao(memoria);

        try
        {
            return await operacao(redis);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Exchange(ref _abertoAteTicks, (clock.UtcNow + TempoAberto).Ticks);
            logger.LogWarning(ex, "REDIS_INDISPONIVEL: bloqueio de login em memória por {Segundos}s (modo degradado)", TempoAberto.TotalSeconds);
            return await operacao(memoria);
        }
    }
}
