using StackExchange.Redis;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Redis;

public sealed class RedisRequestThrottle(IConnectionMultiplexer redis) : IRequestThrottle
{
    private const string Prefixo = "wr:";

    // INCR + PEXPIRE na primeira contagem, atômico no servidor: sem janela em que a chave existe sem TTL.
    private const string ScriptIncremento =
        "local v = redis.call('INCR', KEYS[1]) if v == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end return v";

    public Task<bool> TentarReservarAsync(string chave, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        redis.GetDatabase().StringSetAsync(Prefixo + chave, 1, ttl, When.NotExists);

    public async Task<long> IncrementarAsync(string chave, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var resultado = await redis.GetDatabase().ScriptEvaluateAsync(
            ScriptIncremento,
            [(RedisKey)(Prefixo + chave)],
            [(RedisValue)(long)ttl.TotalMilliseconds]);

        return (long)resultado;
    }

    public async Task<long> LerAsync(string chave, CancellationToken cancellationToken = default)
    {
        var valor = await redis.GetDatabase().StringGetAsync(Prefixo + chave);
        return valor.TryParse(out long numero) ? numero : 0;
    }

    public Task RemoverAsync(string chave, CancellationToken cancellationToken = default) =>
        redis.GetDatabase().KeyDeleteAsync(Prefixo + chave);

    public Task<TimeSpan?> TempoRestanteAsync(string chave, CancellationToken cancellationToken = default) =>
        redis.GetDatabase().KeyTimeToLiveAsync(Prefixo + chave);
}
