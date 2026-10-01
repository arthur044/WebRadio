using WebRadio.Infrastructure.Seguranca;

namespace WebRadio.Api.Infra;

/// <summary>
/// 01 §2.1: com <c>DeveTrocarSenha</c> o token só abre <c>POST /auth/trocar-senha</c> e <c>GET /auth/me</c>
/// (e o refresh, que não usa Bearer). Qualquer outra rota → 403 <c>/erros/troca-de-senha-obrigatoria</c>.
/// Roda depois da autenticação e antes da autorização.
/// </summary>
public static class TrocaDeSenhaObrigatoria
{
    private static readonly string[] Liberadas = ["/api/v1/auth/trocar-senha", "/api/v1/auth/me"];

    public static IApplicationBuilder UseTrocaDeSenhaObrigatoria(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            if (ctx.User.Identity?.IsAuthenticated == true
                && ctx.User.HasClaim(EmissorDeTokens.ClaimDeveTrocarSenha, "true")
                && !Liberadas.Contains(ctx.Request.Path.Value?.TrimEnd('/'), StringComparer.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Status = 403,
                    Type = "/erros/troca-de-senha-obrigatoria",
                    Title = "Troca de senha obrigatória.",
                    Detail = "Troque a senha antes de continuar.",
                }, options: null, contentType: "application/problem+json");
                return;
            }

            await next();
        });
}
