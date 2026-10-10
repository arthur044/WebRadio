using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WebRadio.Api.Erros;
using WebRadio.Infrastructure.Opcoes;
using WebRadio.Infrastructure.Seguranca;

namespace WebRadio.Api.Seguranca;

public static class Politicas
{
    public const string PodeModerar = "PodeModerar";
    public const string SomenteAdmin = "SomenteAdmin";
}

/// <summary>Marca a rota que um token com DeveTrocarSenha pode usar (/auth/trocar-senha e /auth/me).</summary>
public sealed class PermiteSenhaPendenteAttribute : Attribute;

public sealed class SenhaDefinitivaRequirement : IAuthorizationRequirement;

/// <summary>Token com a claim dts (DeveTrocarSenha) só passa nas rotas marcadas com <see cref="PermiteSenhaPendenteAttribute"/>.</summary>
public sealed class SenhaDefinitivaHandler : AuthorizationHandler<SenhaDefinitivaRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SenhaDefinitivaRequirement requirement)
    {
        var pendente = context.User.HasClaim(c => c.Type == JwtService.ClaimDeveTrocarSenha);
        var rotaPermitida = context.Resource is HttpContext http
            && http.GetEndpoint()?.Metadata.GetMetadata<PermiteSenhaPendenteAttribute>() is not null;

        if (!pendente || rotaPermitida)
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, "Troca de senha obrigatória."));
        }

        return Task.CompletedTask;
    }
}

/// <summary>401/403 sempre como ProblemDetails (01-dominio.md §3), com o tipo certo para "troca de senha obrigatória".</summary>
public sealed class RespostaDeAutorizacao : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _padrao = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult resultado)
    {
        if (resultado.Succeeded)
        {
            await _padrao.HandleAsync(next, context, policy, resultado);
            return;
        }

        var problema = resultado switch
        {
            { Challenged: true } => ProblemasHttp.Criar(StatusCodes.Status401Unauthorized, ProblemasHttp.Tipos.NaoAutenticado, "Não autenticado"),
            { AuthorizationFailure: { } falha } when falha.FailedRequirements.OfType<SenhaDefinitivaRequirement>().Any() =>
                ProblemasHttp.Criar(StatusCodes.Status403Forbidden, ProblemasHttp.Tipos.TrocaDeSenhaObrigatoria, "Troca de senha obrigatória", "Troque a senha antes de continuar."),
            _ => ProblemasHttp.Criar(StatusCodes.Status403Forbidden, ProblemasHttp.Tipos.Proibido, "Sem permissão"),
        };

        context.Response.StatusCode = problema.Status!.Value;
        if (resultado.Challenged)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
        }

        context.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(context.Response.Body, problema, cancellationToken: context.RequestAborted);
    }
}

public static class AutenticacaoSetup
{
    public static IServiceCollection AddAutenticacaoWebRadio(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configurado por IOptions<JwtOptions> (já validado no boot): a chave nunca é lida antes da validação.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOpcoes) =>
            {
                var jwt = jwtOpcoes.Value;

                // 05 §6: sem mapeamento de claims, HS256 apenas, e tudo validado.
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    IssuerSigningKey = new SymmetricSecurityKey(jwt.ChaveBytes()),
                    RoleClaimType = "role",
                    NameClaimType = "name",
                };
            });

        services.AddSingleton<IAuthorizationHandler, SenhaDefinitivaHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, RespostaDeAutorizacao>();

        var padrao = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new SenhaDefinitivaRequirement())
            .Build();

        services.AddAuthorizationBuilder()
            // Um .RequireAuthorization() simples usa a DefaultPolicy, nao a Fallback: sem isto o token com dts a contornaria.
            .SetDefaultPolicy(padrao)
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new SenhaDefinitivaRequirement())
                .Build())
            .AddPolicy(Politicas.PodeModerar, p => p
                .RequireRole(nameof(WebRadio.Domain.Enums.Role.Locutor), nameof(WebRadio.Domain.Enums.Role.Admin))
                .AddRequirements(new SenhaDefinitivaRequirement()))
            .AddPolicy(Politicas.SomenteAdmin, p => p
                .RequireRole(nameof(WebRadio.Domain.Enums.Role.Admin))
                .AddRequirements(new SenhaDefinitivaRequirement()));

        return services;
    }
}
