using MediatR;
using WebRadio.Api.Infra;
using WebRadio.Application.Features.Usuarios;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;

namespace WebRadio.Api.Features.Usuarios;

public sealed record CriarUsuarioRequest(string Nome, string Email, string SenhaInicial, Role Role)
{
    public override string ToString() => nameof(CriarUsuarioRequest);
}

public sealed record AlterarUsuarioRequest(Role? Role, bool? Ativo);

/// <summary>02 §3: gestão de usuários, só Admin. Mudar o papel ou desativar derruba as sessões e o hub do usuário.</summary>
public sealed class UsuariosEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var usuarios = app.MapGroup("/api/v1/usuarios").RequireAuthorization("SomenteAdmin");

        usuarios.MapGet("", async (Role? role, string? busca, int? pagina, int? tamanho, HttpContext http, IMediator mediator, CancellationToken ct) =>
        {
            SemCache(http);
            return Results.Ok(await mediator.Send(
                new ListarUsuariosQuery(role, busca, pagina ?? 1, tamanho ?? ResultadoPaginado<UsuarioAdminDto>.TamanhoPadrao), ct));
        });

        usuarios.MapPost("", async (CriarUsuarioRequest req, HttpContext http, IMediator mediator, CancellationToken ct) =>
        {
            var usuario = await mediator.Send(new CriarUsuarioCommand(req.Nome, req.Email, req.SenhaInicial, req.Role), ct);
            return ComETag(http, usuario, StatusCodes.Status201Created); // sem Location: não há GET /usuarios/{id}
        });

        usuarios.MapPatch("/{id:guid}", async (Guid id, AlterarUsuarioRequest req, HttpContext http, IMediator mediator, CancellationToken ct) =>
        {
            // 02, cabeçalho: sem If-Match → 428; If-Match que não é um RowVersion nosso → 412 (ver PreCondicaoIfMatch).
            var versao = PreCondicaoIfMatch.Exigir(http, out var faltou);
            if (faltou is not null) return faltou;

            return ComETag(http, await mediator.Send(new AlterarUsuarioCommand(id, req.Role, req.Ativo, versao!), ct), StatusCodes.Status200OK);
        });

        usuarios.MapPost("/{id:guid}/desbloquear-login", async (Guid id, IMediator mediator, CancellationToken ct) =>
        {
            await mediator.Send(new DesbloquearLoginCommand(id), ct);
            return Results.NoContent();
        });
    }

    private static IResult ComETag(HttpContext http, UsuarioAdminDto usuario, int status)
    {
        http.Response.Headers.ETag = $"\"{usuario.Versao}\"";
        SemCache(http);
        return Results.Json(usuario, statusCode: status);
    }

    // Dados pessoais de terceiros: nunca em cache de navegador nem de proxy.
    private static void SemCache(HttpContext http) => http.Response.Headers.CacheControl = "no-store";
}
