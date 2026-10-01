using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.RateLimiting;
using WebRadio.Api.Infra;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Auth;

namespace WebRadio.Api.Features.Auth;

public sealed record LoginRequest(string Email, string Senha)
{
    public override string ToString() => nameof(LoginRequest);
}

public sealed record RegistrarRequest(string Nome, string Email, string Senha)
{
    public override string ToString() => nameof(RegistrarRequest);
}

/// <summary>02 §2. Refresh, logout e trocar-senha chegam na F07b.</summary>
public sealed class AuthEndpoints : IEndpointModule
{
    public const string CookieRefresh = "__Secure-wr_refresh";
    public const string PoliticaLogin = "login";
    public const string PoliticaRegistrar = "registrar";

    public void Map(IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth");

        auth.MapPost("/login", async (LoginRequest req, HttpContext http, IMediator mediator, IIpHasher ipHasher, CancellationToken ct) =>
            {
                var r = await mediator.Send(new LoginCommand(req.Email, req.Senha, ipHasher.Hash(http.Connection.RemoteIpAddress)), ct);
                DefinirCookie(http, r.Refresh.Valor);
                return Results.Ok(new { accessToken = r.AccessToken, expiraEmUtc = r.ExpiraEmUtc, deveTrocarSenha = r.DeveTrocarSenha, usuario = r.Usuario });
            })
            .AllowAnonymous()
            .RequireRateLimiting(PoliticaLogin);

        auth.MapPost("/registrar", async (RegistrarRequest req, IMediator mediator, CancellationToken ct) =>
            {
                var usuario = await mediator.Send(new RegistrarCommand(req.Nome, req.Email, req.Senha), ct);
                return Results.Created($"/api/v1/auth/me", usuario);
            })
            .AllowAnonymous()
            .RequireRateLimiting(PoliticaRegistrar);

        auth.MapGet("/me", async (ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
            Results.Ok(await mediator.Send(new MeQuery(Guid.Parse(user.FindFirstValue("sub")!)), ct)));
    }

    private static void DefinirCookie(HttpContext http, string valor)
        => http.Response.Cookies.Append(CookieRefresh, valor, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/v1/auth",
            MaxAge = TimeSpan.FromDays(7),
            IsEssential = true,
        });
}
