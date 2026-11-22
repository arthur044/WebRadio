using StackExchange.Redis;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Seguranca;

public sealed class RedisRequestThrottle(IConnectionMultiplexer redis) : IRequestThrottle
{
    private const string Prefixo = "wr:";

    // INCR + PEXPIRE na primeira contagem, atômico no servidor: sem janela em que a chave existe sem TTL.
    private const string ScriptIncremento =
        "local v = redis.call('INCR', KEYS[1]) if v == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end return v";

    // INCRBY + PEXPIRE na criação (a chave só passa a existir aqui), atômico. Piso em 0: devolver bytes/tentativas numa chave que acabou de virar a hora (ou com saldo menor) não deixa o contador negativo
    // (um crédito negativo daria cota extra na hora seguinte; #140). SET ... KEEPTTL mantém a janela.
    private const string ScriptIncrementoPor =
        "local existia = redis.call('EXISTS', KEYS[1]) local v = redis.call('INCRBY', KEYS[1], ARGV[2]) if existia == 0 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end " +
        "if v < 0 then redis.call('SET', KEYS[1], 0, 'KEEPTTL') v = 0 end return v";

    // O SE.Redis não aceita CancellationToken: o WaitAsync(ct) só solta quem chama (o comando já enviado termina no servidor).
    public Task<bool> TentarReservarAsync(string chave, TimeSpan ttl, CancellationToken ct = default) =>
        redis.GetDatabase().StringSetAsync(Prefixo + chave, 1, ttl, When.NotExists).WaitAsync(ct);

    public async Task<long> IncrementarAsync(string chave, TimeSpan ttl, CancellationToken ct = default) =>
        (long)await redis.GetDatabase().ScriptEvaluateAsync(
            ScriptIncremento, [(RedisKey)(Prefixo + chave)], [(RedisValue)(long)ttl.TotalMilliseconds]).WaitAsync(ct);

    public async Task<long> IncrementarPorAsync(string chave, long valor, TimeSpan ttl, CancellationToken ct = default) =>
        (long)await redis.GetDatabase().ScriptEvaluateAsync(
            ScriptIncrementoPor, [(RedisKey)(Prefixo + chave)], [(RedisValue)(long)ttl.TotalMilliseconds, (RedisValue)valor]).WaitAsync(ct);

    public async Task<long> LerAsync(string chave, CancellationToken ct = default) =>
        (await redis.GetDatabase().StringGetAsync(Prefixo + chave).WaitAsync(ct)).TryParse(out long numero) ? numero : 0;

    public Task RemoverAsync(string chave, CancellationToken ct = default) =>
        redis.GetDatabase().KeyDeleteAsync(Prefixo + chave).WaitAsync(ct);

    public Task<TimeSpan?> TempoRestanteAsync(string chave, CancellationToken ct = default) =>
        redis.GetDatabase().KeyTimeToLiveAsync(Prefixo + chave).WaitAsync(ct);
}
