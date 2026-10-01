using Microsoft.AspNetCore.Http.Features;

namespace WebRadio.Api.Infra;

/// <summary>Rotas de auth recebem JSON minúsculo: 8 KB de teto (antes: o padrão do Kestrel, ~30 MB, sobre PBKDF2/validação).</summary>
public static class LimiteDeCorpo
{
    public const long BytesDeAuth = 8 * 1024;

    public static IApplicationBuilder UseLimiteDeCorpoDeAuth(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api/v1/auth", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.Request.ContentLength > BytesDeAuth)
                {
                    ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }

                // Corpo em chunks (sem Content-Length): o Kestrel corta ao passar do teto.
                var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = BytesDeAuth;
            }

            await next();
        });
}
