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

    /// <summary>
    /// <c>/api/v1/internal</c> e <c>/health</c>, comparados por segmento: sem diferenciar maiúsculas e ignorando barras
    /// repetidas (o caminho já chega com o percent-encoding decodificado pelo servidor).
    /// </summary>
    public static bool EhCaminhoInterno(PathString caminho)
    {
        var segmentos = (caminho.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Igual(segmentos, 0, "health")
            || (Igual(segmentos, 0, "api") && Igual(segmentos, 1, "v1") && Igual(segmentos, 2, "internal"));

        static bool Igual(string[] s, int i, string esperado) => s.Length > i && s[i].Equals(esperado, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Depois de UseRouting (precisa do endpoint) e antes de UseRateLimiter/UseAuthentication.</summary>
    public static IApplicationBuilder UseListenerInternoGuard(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            // Duas condições, qualquer uma basta: o endpoint está marcado como interno, OU o caminho cai num prefixo
            // interno mesmo sem casar com rota nenhuma (/api/v1/internal/x, método errado, barra dupla). Sem a segunda,
            // essas variantes cairiam na FallbackPolicy e responderiam 401, revelando que o prefixo existe.
            if (ctx.Connection.LocalPort != PortaInterna
                && (ctx.GetEndpoint()?.Metadata.GetMetadata<ListenerInternoMetadata>() is not null || EhCaminhoInterno(ctx.Request.Path)))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next();
        });
}
