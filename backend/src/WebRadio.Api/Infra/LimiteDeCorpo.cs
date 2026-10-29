using Microsoft.AspNetCore.Http.Features;

namespace WebRadio.Api.Infra;

/// <summary>
/// Rotas de auth, as internas do playout e os pedidos de música recebem JSON minúsculo: 8 KB de teto (antes: o padrão do Kestrel, ~30 MB,
/// sobre PBKDF2/validação). O pedido é anônimo e o corpo passa pela validação do texto ANTES do throttle; o maior corpo legítimo (quatro
/// campos de até 280 caracteres, mesmo escapados como \uXXXX) tem uns 5 KB.
/// </summary>
public static class LimiteDeCorpo
{
    public const long BytesDeAuth = 8 * 1024;

    public static IApplicationBuilder UseLimiteDeCorpoDeAuth(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api/v1/auth", StringComparison.OrdinalIgnoreCase)
                || ctx.Request.Path.StartsWithSegments("/api/v1/internal", StringComparison.OrdinalIgnoreCase)
                || ctx.Request.Path.StartsWithSegments("/api/v1/pedidos", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.Request.ContentLength > BytesDeAuth)
                {
                    ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await ctx.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Status = StatusCodes.Status413PayloadTooLarge,
                        Type = "/erros/corpo-grande-demais",
                        Title = "Corpo grande demais.",
                        Detail = $"O corpo desta rota pode ter no máximo {BytesDeAuth / 1024} KB.",
                    }, options: null, contentType: "application/problem+json");
                    return;
                }

                // Corpo em chunks (sem Content-Length): o Kestrel corta ao passar do teto.
                var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = BytesDeAuth;
            }

            await next();
        });
}
