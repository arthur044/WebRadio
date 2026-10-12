using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WebRadio.Application.Features.Playout;

namespace WebRadio.Infrastructure.Playout;

/// <summary>
/// A memória desta instância é sempre gravada; com Redis, o valor também vai para lá, para a réplica que não recebeu o
/// evento do playout responder o mesmo no <c>/stream/info</c>. Redis fora não é erro para o Liquidsoap: fica o valor local.
/// </summary>
public sealed class TocandoAgoraStore(ILogger<TocandoAgoraStore> logger, IConnectionMultiplexer? redis = null) : ITocandoAgoraStore
{
    private static readonly RedisKey Chave = "wr:tocando-agora";

    /// <summary>Sem evento novo por tanto tempo, o playout parou: melhor "nada tocando" que uma faixa de ontem.</summary>
    public static readonly TimeSpan Validade = TimeSpan.FromHours(6);

    private volatile TocandoAgora? _local;

    public async Task DefinirAsync(TocandoAgora faixa, CancellationToken ct)
    {
        _local = faixa;
        if (redis is null) return;

        try
        {
            await redis.GetDatabase().StringSetAsync(Chave, JsonSerializer.Serialize(faixa), Validade);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "REDIS_INDISPONIVEL: 'tocando agora' ficou só na memória desta instância");
        }
    }

    public async Task<TocandoAgora?> ObterAsync(CancellationToken ct)
    {
        if (redis is null) return Vigente(_local);

        try
        {
            var valor = await redis.GetDatabase().StringGetAsync(Chave);
            return valor.IsNullOrEmpty ? null : JsonSerializer.Deserialize<TocandoAgora>(valor.ToString());
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            return Vigente(_local);
        }
    }

    private static TocandoAgora? Vigente(TocandoAgora? faixa) =>
        faixa is not null && DateTime.UtcNow - faixa.IniciadoEmUtc < Validade ? faixa : null;
}
