using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
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
            // 02, cabeçalho: sem If-Match → 428; If-Match que não é um RowVersion nosso é um valor divergente → 412.
            var ifMatch = http.Request.Headers.IfMatch.ToString();
            if (string.IsNullOrWhiteSpace(ifMatch))
            {
                return Results.Problem(new ProblemDetails
                {
                    Status = StatusCodes.Status428PreconditionRequired,
                    Type = "/erros/precondicao-obrigatoria",
                    Title = "Pré-condição obrigatória.",
                    Detail = "Envie o cabeçalho If-Match com a versão lida do registro.",
                });
            }

            var versao = VersaoDoIfMatch(ifMatch) ?? throw new VersaoDesatualizadaException();
            return ComETag(http, await mediator.Send(new AlterarUsuarioCommand(id, req.Role, req.Ativo, versao), ct), StatusCodes.Status200OK);
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

    /// <summary>Aceita um único ETag forte entre aspas (o que a API emite). Lista, <c>*</c>, <c>W/</c> ou base64 inválido → null.</summary>
    private static byte[]? VersaoDoIfMatch(string ifMatch)
    {
        if (!EntityTagHeaderValue.TryParse(ifMatch, out var etag) || etag.IsWeak) return null;

        var base64 = etag.Tag.ToString().Trim('"');
        var bytes = new byte[base64.Length]; // base64 decodificado nunca é maior que o texto
        return Convert.TryFromBase64String(base64, bytes, out var lidos) ? bytes[..lidos] : null;
    }

    // Dados pessoais de terceiros: nunca em cache de navegador nem de proxy.
    private static void SemCache(HttpContext http) => http.Response.Headers.CacheControl = "no-store";
}
