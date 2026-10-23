using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// D21 em Redis (compartilhado entre réplicas, ao contrário do <see cref="MemoryLoginThrottle"/>). Mesma regra: 5 falhas por
/// (conta, IpHash) bloqueiam por 15 min contados a partir da 5ª; por conta, ≥ 50/h só alerta e atrasa. Chaves sem PII:
/// o e-mail entra como HMAC com o pepper.
/// <para>
/// As três chaves de uma conta (par, bloqueio e contador) levam a MESMA hash tag <c>{conta}</c>: o script Lua toca as três e,
/// num Redis Cluster, chaves de slots diferentes num mesmo EVAL dão CROSSSLOT. O SE.Redis não aceita CancellationToken nas
/// chamadas; o <c>WaitAsync(ct)</c> só solta quem chama (o comando já enviado termina no servidor).
/// </para>
/// </summary>
public sealed class RedisLoginThrottle(IConnectionMultiplexer redis, IIpHasher pseudonimizador, ILogger<RedisLoginThrottle> logger) : ILoginThrottle
{
    // INCR do par (TTL na 1ª falha); na 5ª cria a chave de bloqueio com 15 min; INCR da conta (TTL 1 h). Atômico no servidor.
    // LuaScript.Prepare troca os @nomes por KEYS/ARGV uma vez só (os RedisKey viram KEYS, o resto ARGV).
    private static readonly LuaScript ScriptFalha = LuaScript.Prepare("""
        local falhas = redis.call('INCR', @par)
        if falhas == 1 then redis.call('PEXPIRE', @par, @duracaoDoBloqueio) end
        if falhas >= tonumber(@falhasParaBloquear) then redis.call('SET', @bloqueio, '1', 'PX', @duracaoDoBloqueio) end
        local conta = redis.call('INCR', @conta)
        if conta == 1 then redis.call('PEXPIRE', @conta, @janelaDaConta) end
        return conta
        """);

    public async Task<AvaliacaoDeLogin> AvaliarAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct)
    {
        // Task.WaitAsync devolve a própria task, sem olhar o token, quando ela já terminou: com o Redis rápido as três leituras podem
        // terminar antes do WaitAsync e um token já cancelado passaria sem lançar (o teste de cancelamento ficava instável).
        ct.ThrowIfCancellationRequested();
        var db = redis.GetDatabase();
        var bloqueioTask = db.KeyExistsAsync(ChaveBloqueio(emailNormalizado, ipHash));
        var contaTask = db.StringGetAsync(ChaveConta(emailNormalizado));
        var parTask = db.StringGetAsync(ChavePar(emailNormalizado, ipHash));
        await Task.WhenAll(bloqueioTask, contaTask, parTask).WaitAsync(ct);

        // Atraso só para o par (conta, IP) que já falhou (D21): para a conta inteira, um atacante travaria a vítima.
        var porConta = contaTask.Result.TryParse(out long n) ? n : 0;
        var parJaFalhou = parTask.Result.TryParse(out long f) && f > 0;
        var atraso = parJaFalhou && porConta >= MemoryLoginThrottle.FalhasPorContaParaAtraso
            ? TimeSpan.FromMilliseconds(Math.Min(MemoryLoginThrottle.AtrasoMaximo.TotalMilliseconds, (porConta - MemoryLoginThrottle.FalhasPorContaParaAtraso + 1) * 100))
            : TimeSpan.Zero;

        return new AvaliacaoDeLogin(bloqueioTask.Result, atraso);
    }

    public async Task RegistrarFalhaAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); // o WaitAsync não impede o envio: com o token já cancelado a escrita não pode sair
        var resultado = await ScriptFalha.EvaluateAsync(redis.GetDatabase(), new
        {
            par = (RedisKey)ChavePar(emailNormalizado, ipHash),
            bloqueio = (RedisKey)ChaveBloqueio(emailNormalizado, ipHash),
            conta = (RedisKey)ChaveConta(emailNormalizado),
            duracaoDoBloqueio = (long)MemoryLoginThrottle.DuracaoDoBloqueio.TotalMilliseconds,
            janelaDaConta = (long)MemoryLoginThrottle.JanelaDaConta.TotalMilliseconds,
            falhasParaBloquear = MemoryLoginThrottle.FalhasParaBloquear,
        }).WaitAsync(ct);

        var total = (long)resultado;
        if (total == MemoryLoginThrottle.FalhasPorContaParaAtraso)
        {
            logger.LogWarning("Possível ataque de força bruta: {Falhas} falhas de login em 1 h na conta {ContaId}",
                total, pseudonimizador.Pseudonimo(emailNormalizado));
        }
    }

    public async Task LimparAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Só zera o par: o contador por conta continua, para um atacante não zerá-lo logando na própria conta.
        await redis.GetDatabase().KeyDeleteAsync([ChavePar(emailNormalizado, ipHash), ChaveBloqueio(emailNormalizado, ipHash)]).WaitAsync(ct);
    }

    /// <summary>
    /// Os pares são por IP, que o Admin não conhece: acha as chaves da conta com SCAN (nunca KEYS) em cada primário.
    /// Ação rara e fora do caminho do login. A conta entra como HMAC de tamanho fixo, então o padrão não casa com outra conta.
    /// </summary>
    public async Task DesbloquearContaAsync(string emailNormalizado, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var conta = pseudonimizador.ChaveOpaca(emailNormalizado);
        var db = redis.GetDatabase();
        var chaves = new List<RedisKey> { ChaveConta(emailNormalizado) };

        foreach (var endpoint in redis.GetEndPoints())
        {
            var servidor = redis.GetServer(endpoint);
            if (!servidor.IsConnected || servidor.IsReplica) continue;
            foreach (var padrao in new[] { $"wr:login:{{{conta}}}:falhas:*", $"wr:login:{{{conta}}}:bloqueio:*" })
                await foreach (var chave in servidor.KeysAsync(db.Database, padrao, pageSize: 250).WithCancellation(ct))
                    chaves.Add(chave);
        }

        await db.KeyDeleteAsync([.. chaves]).WaitAsync(ct);
    }

    // {conta} = hash tag: só o trecho entre chaves decide o slot do Cluster. A chave opaca é hexadecimal (sem chaves nem '*').
    private string ChavePar(string email, byte[] ipHash) => $"wr:login:{{{pseudonimizador.ChaveOpaca(email)}}}:falhas:{Convert.ToHexString(ipHash)}";

    private string ChaveBloqueio(string email, byte[] ipHash) => $"wr:login:{{{pseudonimizador.ChaveOpaca(email)}}}:bloqueio:{Convert.ToHexString(ipHash)}";

    private string ChaveConta(string email) => $"wr:login:{{{pseudonimizador.ChaveOpaca(email)}}}:conta";
}
