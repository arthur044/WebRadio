using WebRadio.Infrastructure.Seguranca;

namespace WebRadio.Api.Infra;

/// <summary>
/// 01 §2.1: com <c>DeveTrocarSenha</c> o token só abre <c>POST /auth/trocar-senha</c> e <c>GET /auth/me</c>
/// (e o refresh, que não usa Bearer). Qualquer outra rota → 403 <c>/erros/troca-de-senha-obrigatoria</c>.
/// Roda depois da autenticação e antes da autorização. Quem está liberado é declarado pelo PRÓPRIO endpoint, com
/// <see cref="TrocaDeSenhaObrigatoria.PermitirComTrocaDeSenhaPendente{T}"/> (metadado), e não por uma lista de paths aqui.
/// </summary>
public static class TrocaDeSenhaObrigatoria
{
    /// <summary>Marca o endpoint como acessível ao token restrito pela troca de senha obrigatória.</summary>
    public static TBuilder PermitirComTrocaDeSenhaPendente<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new PermiteComTrocaDeSenhaPendente());

    public static IApplicationBuilder UseTrocaDeSenhaObrigatoria(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            // O UseAuthentication popula ctx.User em TODA requisição com Authorization, inclusive nas rotas AllowAnonymous.
            // Um cliente que anexa o access token a tudo mandaria o token restrito a /auth/refresh e /auth/logout, e o
            // 403 impediria justamente o fluxo "trocar a senha → refresh para perder a restrição". Rotas anônimas
            // (login, registrar, refresh, logout, health) não exigem usuário, então a restrição não se aplica a elas.
            var anonima = ctx.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null;

            if (!anonima
                && ctx.User.Identity?.IsAuthenticated == true
                && ctx.User.HasClaim(EmissorDeTokens.ClaimDeveTrocarSenha, "true")
                && ctx.GetEndpoint()?.Metadata.GetMetadata<PermiteComTrocaDeSenhaPendente>() is null)
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

/// <summary>Metadado de endpoint: vale mesmo com <c>DeveTrocarSenha</c> no token (ver <see cref="TrocaDeSenhaObrigatoria"/>).</summary>
public sealed record PermiteComTrocaDeSenhaPendente;
