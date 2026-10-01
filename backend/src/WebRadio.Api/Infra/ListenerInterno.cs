namespace WebRadio.Api.Infra;

/// <summary>Marca o endpoint como interno: só existe no listener Kestrel <c>:8081</c> (S-A06, S-B05).</summary>
public sealed class ListenerInternoMetadata;

/// <summary>
/// A checagem usa a porta LOCAL do socket, que o cliente não controla. NUNCA <c>RequireHost</c>: ele compara o
/// cabeçalho Host. É um MIDDLEWARE (não endpoint filter) para rodar antes da autenticação: um POST na :8080 sem
/// token recebe 404, não 401, e a existência da rota não vaza.
/// </summary>
public static class ListenerInterno
{
    public const int PortaInterna = 8081;

    public static TBuilder RequireInternalListener<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new ListenerInternoMetadata());

    /// <summary>Depois de UseRouting (precisa do endpoint) e antes de UseRateLimiter/UseAuthentication.</summary>
    public static IApplicationBuilder UseListenerInternoGuard(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            if (ctx.GetEndpoint()?.Metadata.GetMetadata<ListenerInternoMetadata>() is not null
                && ctx.Connection.LocalPort != PortaInterna)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next();
        });
}
