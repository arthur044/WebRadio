using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WebRadio.Api.Infra;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Auth;
using WebRadio.Domain.Erros;

namespace WebRadio.Api.Features.Auth;

public sealed record LoginRequest(string Email, string Senha)
{
    public override string ToString() => nameof(LoginRequest);
}

public sealed record RegistrarRequest(string Nome, string Email, string Senha)
{
    public override string ToString() => nameof(RegistrarRequest);
}

public sealed record TrocarSenhaRequest(string SenhaAtual, string NovaSenha)
{
    public override string ToString() => nameof(TrocarSenhaRequest);
}

/// <summary>02 §2.</summary>
public sealed class AuthEndpoints : IEndpointModule
{
    public const string CookieRefresh = "__Secure-wr_refresh";
    public const string CaminhoDoCookie = "/api/v1/auth";
    public const string PoliticaLogin = "login";
    public const string PoliticaRegistrar = "registrar";
    public const string PoliticaTrocarSenha = "trocar-senha";
    public const string PoliticaSessao = "sessao";

    public void Map(IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth");

        auth.MapPost("/login", async (LoginRequest req, HttpContext http, IMediator mediator, IIpHasher ipHasher, IEmissorDeTokens emissor, CancellationToken ct) =>
            {
                var r = await mediator.Send(new LoginCommand(req.Email, req.Senha, ipHasher.Hash(http.Connection.RemoteIpAddress)), ct);
                return RespostaDeSessao(http, r, emissor);
            })
            .AllowAnonymous()
            .RequireRateLimiting(PoliticaLogin);

        auth.MapPost("/registrar", async (RegistrarRequest req, HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                var usuario = await mediator.Send(new RegistrarCommand(req.Nome, req.Email, req.Senha), ct);
                SemCache(http);
                return Results.Json(usuario, statusCode: StatusCodes.Status201Created); // sem Location: não há recurso público do usuário
            })
            .AllowAnonymous()
            .RequireRateLimiting(PoliticaRegistrar);

        auth.MapPost("/refresh", async (HttpContext http, IMediator mediator, IIpHasher ipHasher, IEmissorDeTokens emissor, IConfiguration config, CancellationToken ct) =>
            {
                if (!OrigemDaRequisicao.EhPermitida(http, config)) return Results.Problem(Proibido());

                var token = http.Request.Cookies[CookieRefresh];
                if (string.IsNullOrEmpty(token)) return SessaoInvalida(http);

                try
                {
                    var r = await mediator.Send(new RefreshCommand(token, ipHasher.Hash(http.Connection.RemoteIpAddress)), ct);
                    return RespostaDeSessao(http, r, emissor);
                }
                catch (RefreshConcorrenteException)
                {
                    // Perdeu a corrida para outra aba: 401 SEM Set-Cookie (apagar sobrescreveria o cookie novo da outra aba) e
                    // com tipo próprio, para o cliente refazer o refresh UMA vez antes de deslogar o usuário.
                    http.Response.Headers.RetryAfter = "0";
                    return Results.Problem(new ProblemDetails
                    {
                        Status = StatusCodes.Status401Unauthorized,
                        Type = "/erros/refresh-concorrente",
                        Title = "Refresh concorrente.",
                        Detail = "Outra aba já renovou a sessão. Tente de novo com o cookie atual.",
                    });
                }
                catch (CredenciaisInvalidasException)
                {
                    // Respondido aqui (não pelo handler global): o handler de exceção limpa os cabeçalhos, inclusive o Set-Cookie.
                    return SessaoInvalida(http);
                }
            })
            .AllowAnonymous()
            .RequireRateLimiting(PoliticaSessao);

        auth.MapPost("/logout", async (HttpContext http, IMediator mediator, IConfiguration config, CancellationToken ct) =>
            {
                if (!OrigemDaRequisicao.EhPermitida(http, config)) return Results.Problem(Proibido());

                await mediator.Send(new LogoutCommand(http.Request.Cookies[CookieRefresh]), ct);
                ApagarCookie(http);
                SemCache(http);
                return Results.NoContent();
            })
            .AllowAnonymous()
            .RequireRateLimiting(PoliticaSessao);

        // Liberada com DeveTrocarSenha (ver TrocaDeSenhaObrigatoria). O cliente chama /auth/refresh logo depois do 204.
        auth.MapPost("/trocar-senha", async (TrocarSenhaRequest req, ClaimsPrincipal user, HttpContext http, IMediator mediator, IIpHasher ipHasher, CancellationToken ct) =>
            {
                if (!Guid.TryParse(user.FindFirstValue("sub"), out var id)) return Results.Unauthorized();

                await mediator.Send(new TrocarSenhaCommand(id, req.SenhaAtual, req.NovaSenha, http.Request.Cookies[CookieRefresh],
                    ipHasher.Hash(http.Connection.RemoteIpAddress)), ct);
                SemCache(http);
                return Results.NoContent();
            })
            .RequireRateLimiting(PoliticaTrocarSenha);

        auth.MapGet("/me", async (ClaimsPrincipal user, HttpContext http, IMediator mediator, CancellationToken ct) =>
        {
            SemCache(http);
            // Token válido sem sub-GUID não acontece (nós emitimos); se acontecer, é não autenticado, não 500.
            return Guid.TryParse(user.FindFirstValue("sub"), out var id)
                ? Results.Ok(await mediator.Send(new MeQuery(id), ct))
                : Results.Unauthorized();
        });
    }

    private static IResult RespostaDeSessao(HttpContext http, LoginResultado r, IEmissorDeTokens emissor)
    {
        http.Response.Cookies.Append(CookieRefresh, r.Refresh.Token, OpcoesDoCookie(emissor.DuracaoDoRefresh));
        SemCache(http);
        return Results.Ok(new { accessToken = r.AccessToken, expiraEmUtc = r.ExpiraEmUtc, deveTrocarSenha = r.DeveTrocarSenha, usuario = r.Usuario });
    }

    private static IResult SessaoInvalida(HttpContext http)
    {
        ApagarCookie(http);
        return Results.Problem(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Type = "/erros/credenciais",
            Title = "Não autorizado.",
            Detail = "Sessão inválida ou expirada.",
        });
    }

    private static ProblemDetails Proibido() => new()
    {
        Status = StatusCodes.Status403Forbidden,
        Type = "/erros/proibido",
        Title = "Sem permissão.",
        Detail = "Origem não permitida.",
    };

    // Respostas de auth têm token/dados do usuário: nunca em cache de navegador nem de proxy.
    private static void SemCache(HttpContext http) => http.Response.Headers.CacheControl = "no-store";

    private static void ApagarCookie(HttpContext http) => http.Response.Cookies.Delete(CookieRefresh, OpcoesDoCookie(null));

    private static CookieOptions OpcoesDoCookie(TimeSpan? duracao) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = CaminhoDoCookie,
        MaxAge = duracao,
        IsEssential = true,
    };
}
