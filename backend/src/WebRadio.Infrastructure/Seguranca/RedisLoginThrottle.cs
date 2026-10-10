using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// D21 em Redis (compartilhado entre réplicas, ao contrário do <see cref="MemoryLoginThrottle"/>). Mesma regra: 5 falhas por
/// (conta, IpHash) bloqueiam por 15 min contados a partir da 5ª; por conta, ≥ 50/h só alerta e atrasa. Chaves sem PII:
/// o e-mail entra como HMAC com o pepper.
/// </summary>
public sealed class RedisLoginThrottle(IConnectionMultiplexer redis, IIpHasher pseudonimizador, ILogger<RedisLoginThrottle> logger) : ILoginThrottle
{
    // INCR do par (TTL na 1ª falha); na 5ª cria a chave de bloqueio com 15 min; INCR da conta (TTL 1 h). Atômico no servidor.
    private const string ScriptFalha = """
        local falhas = redis.call('INCR', KEYS[1])
        if falhas == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end
        if falhas >= tonumber(ARGV[3]) then redis.call('SET', KEYS[2], '1', 'PX', ARGV[1]) end
        local conta = redis.call('INCR', KEYS[3])
        if conta == 1 then redis.call('PEXPIRE', KEYS[3], ARGV[2]) end
        return conta
        """;

    public async Task<AvaliacaoDeLogin> AvaliarAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var bloqueioTask = db.KeyExistsAsync(ChaveBloqueio(emailNormalizado, ipHash));
        var contaTask = db.StringGetAsync(ChaveConta(emailNormalizado));
        await Task.WhenAll(bloqueioTask, contaTask);

        var porConta = contaTask.Result.TryParse(out long n) ? n : 0;
        var atraso = porConta >= MemoryLoginThrottle.FalhasPorContaParaAtraso
            ? TimeSpan.FromMilliseconds(Math.Min(5000, (porConta - MemoryLoginThrottle.FalhasPorContaParaAtraso + 1) * 100))
            : TimeSpan.Zero;

        return new AvaliacaoDeLogin(bloqueioTask.Result, atraso);
    }

    public async Task RegistrarFalhaAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct)
    {
        var resultado = await redis.GetDatabase().ScriptEvaluateAsync(
            ScriptFalha,
            [ChavePar(emailNormalizado, ipHash), ChaveBloqueio(emailNormalizado, ipHash), ChaveConta(emailNormalizado)],
            [(long)MemoryLoginThrottle.DuracaoDoBloqueio.TotalMilliseconds, (long)MemoryLoginThrottle.JanelaDaConta.TotalMilliseconds, MemoryLoginThrottle.FalhasParaBloquear]);

        var total = (long)resultado;
        if (total == MemoryLoginThrottle.FalhasPorContaParaAtraso)
        {
            logger.LogWarning("Possível ataque de força bruta: {Falhas} falhas de login em 1 h na conta {ContaId}",
                total, pseudonimizador.Pseudonimo(emailNormalizado));
        }
    }

    public async Task LimparAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct) =>
        // Só zera o par: o contador por conta continua, para um atacante não zerá-lo logando na própria conta.
        await redis.GetDatabase().KeyDeleteAsync([ChavePar(emailNormalizado, ipHash), ChaveBloqueio(emailNormalizado, ipHash)]);

    private string ChavePar(string email, byte[] ipHash) => $"wr:login:falhas:{pseudonimizador.ChaveOpaca(email)}:{Convert.ToHexString(ipHash)}";

    private string ChaveBloqueio(string email, byte[] ipHash) => $"wr:login:bloqueio:{pseudonimizador.ChaveOpaca(email)}:{Convert.ToHexString(ipHash)}";

    private string ChaveConta(string email) => $"wr:login:conta:{pseudonimizador.ChaveOpaca(email)}";
}
