using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using WebRadio.Api.Infra;
using WebRadio.Application.Features.Midias;
using WebRadio.Domain.Enums;

namespace WebRadio.Api.Features.Midias;

/// <summary>Corpo do <c>POST /midias/uploads</c> (02 §6). Campo ausente vira 400 (a validação confere o resto).</summary>
public sealed record IniciarUploadRequest(string? NomeOriginal, TipoMidia? TipoMidia, long? TamanhoBytes, string? MimeTypeDeclarado, string? Titulo, string? Artista);

/// <summary>
/// 02 §6. Tudo é do <c>PodeModerar</c> (Locutor e Admin); o <c>Admin</c> enxerga e conclui o upload de qualquer um e é o único que exclui, ativa e desativa.
/// O <c>PATCH</c> lê o corpo como JSON solto para distinguir campo ausente (não muda) de vazio (limpa).
/// </summary>
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

        g.MapGet("", async (TipoMidia? tipo, StatusSanitizacao? status, bool? ativo, string? busca, int? pagina, int? tamanho, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
            Results.Ok(await mediator.Send(new ListarMidiasQuery(tipo, status, ativo, busca, pagina ?? 1, tamanho ?? 20, user.IsInRole("Admin")), ct)));

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
            ComETag(http, await mediator.Send(new MidiaPorIdQuery(id, user.IsInRole("Admin")), ct), StatusCodes.Status200OK));

        // O <audio src> não manda Bearer: com ?formato=url o cliente (que tem o Bearer) pega a URL pré-assinada em JSON e a usa direto. Sem o parâmetro, 302.
        g.MapGet("/{id:guid}/preview", async (Guid id, string? formato, HttpContext http, IMediator mediator, CancellationToken ct) =>
        {
            if (formato is not null && !formato.Equals("url", StringComparison.Ordinal))
                throw new ValidationException([new ValidationFailure("formato", "formato só aceita \"url\".")]);
            var preview = await mediator.Send(new PreviewDaMidiaQuery(id), ct);
            http.Response.Headers.CacheControl = "no-store"; // a URL é assinada e curta: nunca fica em cache
            return formato is null ? Results.Redirect(preview.Url) : Results.Json(preview);
        });

        g.MapPatch("/{id:guid}", async (Guid id, JsonElement corpo, HttpContext http, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
        {
            var versao = PreCondicaoIfMatch.Exigir(http, out var faltou);
            if (faltou is not null) return faltou;
            if (corpo.ValueKind != JsonValueKind.Object) throw new ValidationException([new ValidationFailure("corpo", "Envie um objeto JSON.")]);

            var (titulo, tituloPresente) = Texto(corpo, "titulo");
            var (artista, artistaPresente) = Texto(corpo, "artista");
            bool? ativo = null;
            if (corpo.TryGetProperty("ativo", out var a))
            {
                if (a.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ValidationException([new ValidationFailure("ativo", "ativo precisa ser true ou false.")]);
                ativo = a.GetBoolean();
            }

            var dto = await mediator.Send(new EditarMidiaCommand(id, versao!, titulo, artista, ativo, user.IsInRole("Admin"), tituloPresente, artistaPresente), ct);
            return ComETag(http, dto, StatusCodes.Status200OK);
        });

        g.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub"), out var admin)) return Results.Unauthorized();
            var r = await mediator.Send(new ExcluirMidiaCommand(id, admin), ct);
            return r.Excluida ? Results.NoContent() : Results.Ok(r.Midia);
        }).RequireAuthorization("SomenteAdmin");

        g.MapPost("/{id:guid}/concluir", async (Guid id, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub"), out var usuario)) return Results.Unauthorized();
            await mediator.Send(new ConcluirUploadCommand(id, usuario, user.IsInRole("Admin")), ct);
            return Results.Accepted();
        });
    }

    /// <summary>String ou null (e se a propriedade existe). Outro tipo → 400.</summary>
    private static (string? Valor, bool Presente) Texto(JsonElement corpo, string nome)
    {
        if (!corpo.TryGetProperty(nome, out var v)) return (null, false);
        return v.ValueKind switch
        {
            JsonValueKind.String => (v.GetString(), true),
            JsonValueKind.Null => (null, true),
            _ => throw new ValidationException([new ValidationFailure(nome, $"{nome} precisa ser texto.")]),
        };
    }

    private static IResult ComETag(HttpContext http, MidiaDto dto, int status)
    {
        PreCondicaoIfMatch.DefinirETag(http, dto.Versao);
        return Results.Json(dto, statusCode: status);
    }
}
