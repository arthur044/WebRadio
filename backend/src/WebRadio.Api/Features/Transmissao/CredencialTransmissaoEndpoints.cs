using System.Security.Claims;
using MediatR;
using Microsoft.Extensions.Options;
using WebRadio.Api.Infra;
using WebRadio.Application.Features.Harbor;
using WebRadio.Application.Features.Transmissao;

namespace WebRadio.Api.Features.Transmissao;

public sealed record GerarCredencialRequest(string? SenhaAtual)
{
    public override string ToString() => nameof(GerarCredencialRequest);
}

/// <summary><c>GET</c> do próprio locutor: a credencial (nunca o segredo) e o <c>conexao</c> que ele digita no source client.</summary>
public sealed record CredencialResposta(Guid Identificador, DateTime CriadaEmUtc, DateTime? UltimoUsoEmUtc, ConexaoDoHarbor Conexao);

/// <summary><c>POST</c>: a única resposta com o segredo. O <c>ToString</c> o esconde (um log da resposta não o vaza).</summary>
public sealed record CredencialGeradaResposta(Guid Identificador, string Segredo, DateTime CriadaEmUtc, ConexaoDoHarbor Conexao)
{
    public override string ToString() => $"{nameof(CredencialGeradaResposta)} {{ Identificador = {Identificador} }}";
}

/// <summary>
/// 02 §4 (06 "REST pública"): credencial de transmissão do source client. Sem <c>PermitirComTrocaDeSenhaPendente</c>: o token
/// restrito da troca de senha recebe 403. Toda resposta é <c>no-store</c>; o segredo só existe na resposta do <c>POST</c>.
/// </summary>
public sealed class CredencialTransmissaoEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var minha = app.MapGroup("/api/v1/locutores/me/credencial-transmissao").RequireAuthorization("PodeModerar");

        minha.MapGet("", async (ClaimsPrincipal user, HttpContext http, IMediator mediator, IOptions<HarborOptions> harbor, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub"), out var id)) return Results.Unauthorized();
            SemCache(http);
            return await mediator.Send(new ObterMinhaCredencialQuery(id), ct) is { } dto
                ? Results.Ok(new CredencialResposta(dto.Identificador, dto.CriadaEmUtc, dto.UltimoUsoEmUtc, harbor.Value.Conexao()))
                : Results.NoContent();
        });

        minha.MapPost("", async (GerarCredencialRequest req, ClaimsPrincipal user, HttpContext http, IMediator mediator, IOptions<HarborOptions> harbor, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub"), out var id)) return Results.Unauthorized();
            SemCache(http);
            var gerada = await mediator.Send(new GerarCredencialCommand(id, req.SenhaAtual ?? ""), ct);
            return Results.Json(new CredencialGeradaResposta(gerada.Identificador, gerada.Segredo, gerada.CriadaEmUtc, harbor.Value.Conexao()),
                statusCode: StatusCodes.Status201Created); // sem Location: o segredo não é recurso consultável
        });

        minha.MapDelete("", async (ClaimsPrincipal user, HttpContext http, IMediator mediator, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub"), out var id)) return Results.Unauthorized();
            await mediator.Send(new RevogarCredencialCommand(id, id), ct);
            SemCache(http);
            return Results.NoContent();
        });

        app.MapGet("/api/v1/credenciais-transmissao", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                SemCache(http);
                return Results.Ok(await mediator.Send(new ListarCredenciaisAtivasQuery(), ct));
            })
            .RequireAuthorization("SomenteAdmin");

        app.MapDelete("/api/v1/usuarios/{id:guid}/credencial-transmissao", async (Guid id, ClaimsPrincipal user, HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                if (!Guid.TryParse(user.FindFirstValue("sub"), out var adminId)) return Results.Unauthorized();
                await mediator.Send(new RevogarCredencialCommand(id, adminId), ct);
                SemCache(http);
                return Results.NoContent();
            })
            .RequireAuthorization("SomenteAdmin");
    }

    private static void SemCache(HttpContext http) => http.Response.Headers.CacheControl = "no-store";
}
