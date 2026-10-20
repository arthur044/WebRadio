using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Harbor;

namespace WebRadio.Infrastructure.Harbor;

/// <summary>
/// Como o <c>TocandoAgoraStore</c>: a memória desta instância é sempre gravada; com Redis, o estado também vai para lá, para a
/// réplica que não recebeu o evento do harbor responder o mesmo no <c>/stream/info</c>. Redis fora fica com o valor local.
/// </summary>
public sealed class EstadoDoAoVivoStore(IClock clock, ILogger<EstadoDoAoVivoStore> logger, IConnectionMultiplexer? redis = null) : IEstadoDoAoVivoStore
{
    private static readonly RedisKey Chave = "wr:ao-vivo";

    /// <summary>Duração máxima de um programa (12 h) + folga: um estado mais velho que isso não diz nada.</summary>
    public static readonly TimeSpan Validade = TimeSpan.FromHours(13);

    private const string TrocarSeIgual = "if redis.call('GET', KEYS[1]) == ARGV[1] then redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3]) return 1 else return 0 end";

    private volatile EstadoDoAoVivo? _local;

    public async Task DefinirAsync(EstadoDoAoVivo estado, CancellationToken ct)
    {
        _local = estado;
        if (redis is null) return;

        try
        {
            await redis.GetDatabase().StringSetAsync(Chave, JsonSerializer.Serialize(estado), Validade);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "REDIS_INDISPONIVEL: o estado do ao vivo ficou só na memória desta instância");
        }
    }

    public async Task<ExpiracaoDoAoVivo?> ExpirarVencidoAsync(CancellationToken ct)
    {
        if (redis is null) return ExpirarLocal();

        try
        {
            var db = redis.GetDatabase();
            var valor = await db.StringGetAsync(Chave);
            if (valor.IsNullOrEmpty) return ExpirarLocal();

            var antes = JsonSerializer.Deserialize<EstadoDoAoVivo>(valor.ToString());
            if (antes is null || antes.Vigente(clock.UtcNow) == antes) return null;

            var depois = EstadoDoAoVivo.ForaDoAr(antes.DesdeUtc);
            // Só grava se ninguém mudou o valor desde a leitura (um Conectado novo, ou outra réplica já expirou): quem perde não
            // publica. Um script Lua, e não Condition/ValueCondition: o Redis do compose é anterior ao 8.4 e a transação com WATCH
            // não é atômica no servidor.
            if (!await TrocarSeIgualAsync(db, valor.ToString(), depois)) return null;

            _local = depois;
            return new ExpiracaoDoAoVivo(antes, depois);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            logger.LogWarning(ex, "REDIS_INDISPONIVEL: o vencimento do ao vivo foi avaliado só com a memória desta instância");
            return ExpirarLocal();
        }
    }

    private async Task<bool> TrocarSeIgualAsync(IDatabase db, string esperado, EstadoDoAoVivo novo) =>
        (long)await db.ScriptEvaluateAsync(TrocarSeIgual, [Chave], [esperado, JsonSerializer.Serialize(novo), (long)Validade.TotalMilliseconds]) == 1;

    public async Task DesfazerExpiracaoAsync(ExpiracaoDoAoVivo troca, CancellationToken ct)
    {
        if (_local == troca.Depois) _local = troca.Antes;
        if (redis is null) return;

        try
        {
            await TrocarSeIgualAsync(redis.GetDatabase(), JsonSerializer.Serialize(troca.Depois), troca.Antes);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "REDIS_INDISPONIVEL: o estado vencido não voltou ao Redis; a próxima passada o reavalia");
        }
    }

    private ExpiracaoDoAoVivo? ExpirarLocal()
    {
        var antes = _local;
        if (antes is null || antes.Vigente(clock.UtcNow) == antes) return null;

        var depois = EstadoDoAoVivo.ForaDoAr(antes.DesdeUtc);
        _local = depois;
        return new ExpiracaoDoAoVivo(antes, depois);
    }

    public async Task<EstadoDoAoVivo?> ObterAsync(CancellationToken ct)
    {
        if (redis is null) return _local?.Vigente(clock.UtcNow);

        try
        {
            var valor = await redis.GetDatabase().StringGetAsync(Chave);
            var estado = valor.IsNullOrEmpty ? _local : JsonSerializer.Deserialize<EstadoDoAoVivo>(valor.ToString());
            return estado?.Vigente(clock.UtcNow);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            return _local?.Vigente(clock.UtcNow);
        }
    }
}
