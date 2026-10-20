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
