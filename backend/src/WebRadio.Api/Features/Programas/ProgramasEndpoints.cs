using System.Globalization;
using System.Security.Claims;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using WebRadio.Api.Infra;
using WebRadio.Application.Features.Programas;

namespace WebRadio.Api.Features.Programas;

public sealed record ProgramaRequest(string? Titulo, string? Descricao, Guid LocutorId, DateTime InicioUtc, DateTime FimUtc);

/// <summary>
/// 02 §4 e a matriz RBAC do §1: a grade e o programa ao vivo são públicos; criar, editar e cancelar são do Admin; encerrar é do
/// Locutor dono ou do Admin (a posse é checada no handler). Conflito de horário → 409 com <c>programaConflitanteId</c> (tratador).
/// </summary>
public sealed class ProgramasEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/programas");

        g.MapGet("", async (string? deUtc, string? ateUtc, bool? incluirCancelados, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
            {
                // Query string em string: o binder de DateTime converteria "…Z" para a hora local e perderia o Kind=Utc.
                var de = LerUtc(deUtc, "deUtc");
                var ate = LerUtc(ateUtc, "ateUtc");
                // incluirCancelados só vale para Admin (02 §4); para os outros o parâmetro é ignorado, não é erro.
                var admin = user.IsInRole("Admin");
                return Results.Ok(await mediator.Send(new ListarProgramasQuery(de, ate, incluirCancelados == true && admin), ct));
            })
            .AllowAnonymous();

        g.MapGet("/ao-vivo", async (IMediator mediator, CancellationToken ct) =>
                await mediator.Send(new ProgramaAoVivoQuery(), ct) is { } programa ? Results.Ok(programa) : Results.NoContent())
            .AllowAnonymous();

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, IMediator mediator, CancellationToken ct) =>
                ComETag(http, await mediator.Send(new ProgramaPorIdQuery(id), ct), StatusCodes.Status200OK))
            .AllowAnonymous();

        g.MapPost("", async (ProgramaRequest req, HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                var programa = await mediator.Send(new CriarProgramaCommand(req.Titulo ?? "", req.Descricao, req.LocutorId, req.InicioUtc, req.FimUtc), ct);
                http.Response.Headers.Location = $"/api/v1/programas/{programa.Id}";
                return ComETag(http, programa, StatusCodes.Status201Created);
            })
            .RequireAuthorization("SomenteAdmin");

        g.MapPut("/{id:guid}", async (Guid id, ProgramaRequest req, HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                var versao = PreCondicaoIfMatch.Exigir(http, out var faltou);
                if (faltou is not null) return faltou;
                return ComETag(http,
                    await mediator.Send(new EditarProgramaCommand(id, req.Titulo ?? "", req.Descricao, req.LocutorId, req.InicioUtc, req.FimUtc, versao!), ct),
                    StatusCodes.Status200OK);
            })
            .RequireAuthorization("SomenteAdmin");

        g.MapPost("/{id:guid}/cancelar", async (Guid id, HttpContext http, IMediator mediator, CancellationToken ct) =>
                ComETag(http, await mediator.Send(new CancelarProgramaCommand(id), ct), StatusCodes.Status200OK))
            .RequireAuthorization("SomenteAdmin");

        g.MapPost("/{id:guid}/encerrar", async (Guid id, ClaimsPrincipal user, HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                if (!Guid.TryParse(user.FindFirstValue("sub"), out var solicitante)) return Results.Unauthorized();
                return ComETag(http, await mediator.Send(new EncerrarProgramaCommand(id, solicitante, user.IsInRole("Admin")), ct), StatusCodes.Status200OK);
            })
            .RequireAuthorization("PodeModerar");
    }

    private static IResult ComETag(HttpContext http, ProgramaDto programa, int status)
    {
        PreCondicaoIfMatch.DefinirETag(http, programa.Versao);
        return Results.Json(programa, statusCode: status);
    }

    /// <summary>"2026-10-20T10:00:00Z" → <c>Kind = Utc</c>. Sem o sufixo Z (ou com offset), ou ilegível → 400 de validação.</summary>
    private static DateTime LerUtc(string? valor, string campo)
    {
        if (string.IsNullOrWhiteSpace(valor))
            throw new ValidationException([new ValidationFailure(campo, "Informe o horário em UTC (terminar em Z).")]);
        if (!valor.EndsWith('Z') || !DateTime.TryParse(valor, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var data) || data.Kind != DateTimeKind.Utc)
            throw new ValidationException([new ValidationFailure(campo, "O horário precisa estar em UTC (terminar em Z).")]);
        return data;
    }
}
