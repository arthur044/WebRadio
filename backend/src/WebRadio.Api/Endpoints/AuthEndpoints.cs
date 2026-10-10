using System.Security.Claims;
using MediatR;
using WebRadio.Api.Erros;
using WebRadio.Api.Seguranca;
using WebRadio.Application.Autenticacao;
using WebRadio.Application.Comum;

namespace WebRadio.Api.Endpoints;

public sealed record LoginRequest(string? Email, string? Senha);

public sealed record RegistrarRequest(string? Nome, string? Email, string? Senha);

public sealed record TrocarSenhaRequest(string? SenhaAtual, string? NovaSenha);

/// <summary>02-api-rest §2.</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/v1/auth").WithTags("Auth").WithMetadata(new LimiteDeCorpoAttribute(4096));

        grupo.MapPost("/registrar", Registrar).AllowAnonymous().RequireRateLimiting(LimiteDeRequisicoes.Registrar);
        grupo.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting(LimiteDeRequisicoes.Login);
        grupo.MapPost("/refresh", Refresh).AllowAnonymous().RequireRateLimiting(LimiteDeRequisicoes.Sessao);
        grupo.MapPost("/logout", Logout).AllowAnonymous().RequireRateLimiting(LimiteDeRequisicoes.Sessao);

        // Únicas rotas liberadas enquanto DeveTrocarSenha = true.
        grupo.MapPost("/trocar-senha", TrocarSenha).WithMetadata(new PermiteSenhaPendenteAttribute()).RequireAuthorization()
            .RequireRateLimiting(LimiteDeRequisicoes.TrocarSenha);
        grupo.MapGet("/me", Me).WithMetadata(new PermiteSenhaPendenteAttribute()).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> Registrar(RegistrarRequest corpo, ISender sender, CancellationToken ct)
    {
        var usuario = await sender.Send(new RegistrarCommand(corpo.Nome ?? string.Empty, corpo.Email ?? string.Empty, corpo.Senha ?? string.Empty), ct);
        return Results.Json(usuario, statusCode: StatusCodes.Status201Created); // sem Location: /auth/me e do usuario autenticado, nao do novo
    }

    private static async Task<IResult> Login(LoginRequest corpo, HttpContext http, ISender sender, CancellationToken ct)
    {
        var sessao = await sender.Send(new LoginCommand(corpo.Email ?? string.Empty, corpo.Senha ?? string.Empty, http.Connection.RemoteIpAddress), ct);
        return RespostaDeSessao(http, sessao);
    }

    private static async Task<IResult> Refresh(HttpContext http, ISender sender, CancellationToken ct)
    {
        if (!CookieRefresh.OrigemPermitida(http))
        {
            return Results.Problem(ProblemasHttp.Criar(StatusCodes.Status403Forbidden, ProblemasHttp.Tipos.Proibido, "Sem permissão"));
        }

        var token = CookieRefresh.Ler(http);
        if (token is null)
        {
            return CredenciaisInvalidas(http);
        }

        try
        {
            var sessao = await sender.Send(new RefreshCommand(token, http.Connection.RemoteIpAddress), ct);
            return RespostaDeSessao(http, sessao);
        }
        catch (RefreshConcorrenteException)
        {
            // Perdeu a corrida para outra aba: 401 SEM Set-Cookie. Apagar aqui sobrescreveria o cookie novo gravado pela outra aba.
            // Tipo proprio: o cliente deve refazer o refresh UMA vez com o cookie atual antes de deslogar o usuario.
            http.Response.Headers.RetryAfter = "0";
            return Results.Problem(ProblemasHttp.Criar(
                StatusCodes.Status401Unauthorized, ProblemasHttp.Tipos.RefreshConcorrente, "Refresh concorrente",
                "Outra aba já renovou a sessão. Tente de novo com o cookie atual."));
        }
        catch (CredenciaisInvalidasException)
        {
            // Respondido aqui (não pelo handler global) porque o handler de exceção limpa os cabeçalhos, inclusive o Set-Cookie.
            return CredenciaisInvalidas(http);
        }
    }

    private static async Task<IResult> Logout(HttpContext http, ISender sender, CancellationToken ct)
    {
        if (!CookieRefresh.OrigemPermitida(http))
        {
            return Results.Problem(ProblemasHttp.Criar(StatusCodes.Status403Forbidden, ProblemasHttp.Tipos.Proibido, "Sem permissão"));
        }

        await sender.Send(new LogoutCommand(CookieRefresh.Ler(http)), ct);
        CookieRefresh.Apagar(http);
        return Results.NoContent();
    }

    private static async Task<IResult> TrocarSenha(TrocarSenhaRequest corpo, HttpContext http, ISender sender, CancellationToken ct)
    {
        var comando = new TrocarSenhaCommand(
            IdDoUsuario(http), corpo.SenhaAtual ?? string.Empty, corpo.NovaSenha ?? string.Empty, CookieRefresh.Ler(http), http.Connection.RemoteIpAddress);

        await sender.Send(comando, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Me(HttpContext http, ISender sender, CancellationToken ct) =>
        Results.Ok(await sender.Send(new ObterMeQuery(IdDoUsuario(http)), ct));

    private static IResult RespostaDeSessao(HttpContext http, SessaoIniciada sessao)
    {
        CookieRefresh.Escrever(http, sessao.Refresh.TokenBruto, sessao.Refresh.ExpiraEmUtc);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(sessao.Resposta);
    }

    private static IResult CredenciaisInvalidas(HttpContext http)
    {
        CookieRefresh.Apagar(http);
        return Results.Problem(ProblemasHttp.Credenciais());
    }

    private static Guid IdDoUsuario(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue("sub"), out var id) ? id : throw new CredenciaisInvalidasException();
}
