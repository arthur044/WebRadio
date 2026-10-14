using StackExchange.Redis;
using Xunit;

namespace WebRadio.Infrastructure.Tests.Suporte;

/// <summary>
/// Redis real para os testes do throttle (o Lua, as hash tags e o SCAN não existem no dublê). Usa WEBRADIO_TEST_REDIS
/// (host:porta). Definida, o servidor é OBRIGATÓRIO: se não responder os [RedisFact] FALHAM (o job do CI não fica verde só
/// com testes ignorados). Sem a variável, aparecem como ignorados com o motivo.
/// </summary>
public static class RedisDeTeste
{
    private static readonly Lazy<IConnectionMultiplexer?> Conexao = new(Conectar);

    public static bool Disponivel => Conexao.Value is not null;

    public static IConnectionMultiplexer Multiplexador => Conexao.Value!;

    private static IConnectionMultiplexer? Conectar()
    {
        var destino = Environment.GetEnvironmentVariable("WEBRADIO_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(destino)) return null;

        try
        {
            return ConnectionMultiplexer.Connect(new ConfigurationOptions
            {
                EndPoints = { destino },
                AbortOnConnectFail = true,
                ConnectTimeout = 5000,
            });
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"WEBRADIO_TEST_REDIS está definida, mas o Redis não respondeu: {e.Message}", e);
        }
    }
}

/// <summary>[Fact] que só roda com Redis disponível; senão aparece como ignorado, nunca como verde falso.</summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (!RedisDeTeste.Disponivel) Skip = "Redis indisponível (defina WEBRADIO_TEST_REDIS=host:porta).";
    }
}
