using System.Security.Claims;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using WebRadio.Api.Infra;
using WebRadio.Application.Features.Midias;
using WebRadio.Domain.Enums;

namespace WebRadio.Api.Features.Midias;

/// <summary>Corpo do <c>POST /midias/uploads</c> (02 §6). Campo ausente vira 400 (a validação confere o resto).</summary>
public sealed record IniciarUploadRequest(string? NomeOriginal, TipoMidia? TipoMidia, long? TamanhoBytes, string? MimeTypeDeclarado, string? Titulo, string? Artista);

/// <summary>02 §6. Tudo é do <c>PodeModerar</c> (Locutor e Admin); o <c>Admin</c> enxerga e conclui o upload de qualquer um.</summary>
public sealed class MidiasEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/midias").RequireAuthorization("PodeModerar");

        g.MapPost("/uploads", async (IniciarUploadRequest req, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub"), out var usuario)) return Results.Unauthorized();
            List<ValidationFailure> faltas = [];
            if (req.TipoMidia is null) faltas.Add(new("tipoMidia", "tipoMidia é obrigatório."));
            if (req.TamanhoBytes is null) faltas.Add(new("tamanhoBytes", "tamanhoBytes é obrigatório."));
            if (faltas.Count > 0) throw new ValidationException(faltas);

            var iniciado = await mediator.Send(new IniciarUploadCommand(
                usuario, req.NomeOriginal ?? "", req.Titulo, req.Artista, req.TipoMidia!.Value, req.MimeTypeDeclarado ?? "", req.TamanhoBytes!.Value), ct);
            return Results.Json(iniciado, statusCode: StatusCodes.Status201Created);
        });

        g.MapPost("/{id:guid}/concluir", async (Guid id, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub"), out var usuario)) return Results.Unauthorized();
            await mediator.Send(new ConcluirUploadCommand(id, usuario, user.IsInRole("Admin")), ct);
            return Results.Accepted();
        });
    }
}
