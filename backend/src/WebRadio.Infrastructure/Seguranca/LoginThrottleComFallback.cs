using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// S-A12: Redis fora do ar não derruba o login. Na primeira falha o disjuntor abre por 30 s e as chamadas vão direto
/// para o throttle em memória (sem pagar o timeout do Redis a cada login). Passados os 30 s ele fica MEIO-ABERTO: uma única
/// chamada sonda o Redis (as demais seguem na memória enquanto ela estiver em voo); se a sonda dá certo o disjuntor fecha,
/// se falha ele reabre por mais 30 s. Sem a sonda única, todas as chamadas simultâneas pagariam o timeout de uma vez.
/// O evento fica no log, com o TIPO da exceção (a mensagem do cliente do Redis pode trazer o endereço).
/// </summary>
public sealed class LoginThrottleComFallback(ILoginThrottle redis, ILoginThrottle memoria, IClock clock, ILogger<LoginThrottleComFallback> logger) : ILoginThrottle
{
    public static readonly TimeSpan TempoAberto = TimeSpan.FromSeconds(30);

    private long _abertoAteTicks; // 0 = fechado (nunca abriu, ou a última sonda deu certo)
    private int _sondaEmVoo;

    public bool RedisEmUso => Volatile.Read(ref _abertoAteTicks) is var ate && (ate == 0 || clock.UtcNow.Ticks >= ate);

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
        var sonda = false;
        var ate = Volatile.Read(ref _abertoAteTicks);
        if (ate != 0)
        {
            if (clock.UtcNow.Ticks < ate) return await operacao(memoria);
            // Meio-aberto: só uma chamada sonda o Redis.
            if (Interlocked.CompareExchange(ref _sondaEmVoo, 1, 0) != 0) return await operacao(memoria);
            sonda = true;
        }

        try
        {
            var resultado = await operacao(redis);
            if (sonda) Volatile.Write(ref _abertoAteTicks, 0); // a sonda deu certo: fecha
            return resultado;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Volatile.Write(ref _abertoAteTicks, (clock.UtcNow + TempoAberto).Ticks);
            logger.LogWarning("REDIS_INDISPONIVEL ({Tipo}): bloqueio de login em memória por {Segundos}s (modo degradado)", ex.GetType().Name, TempoAberto.TotalSeconds);
            return await operacao(memoria);
        }
        finally
        {
            if (sonda) Volatile.Write(ref _sondaEmVoo, 0);
        }
    }
}
