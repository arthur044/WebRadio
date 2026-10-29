using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using WebRadio.Api.Infra;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Divulgacoes;
using WebRadio.Domain.Erros;

namespace WebRadio.Api.Features.Divulgacoes;

/// <summary>O corpo do <c>POST</c> e do <c>PUT</c> (02 §7). <c>PUT</c> substitui o recurso inteiro.</summary>
public sealed record DivulgacaoRequest(
    string? Titulo, string? Mensagem, string? ImagemChave, string? LinkDestino, byte? Prioridade,
    DateTime? InicioExibicaoUtc, DateTime? FimExibicaoUtc, bool? Ativo);

/// <summary>
/// 02 §7. As ativas são públicas (<c>Cache-Control: public, max-age=30</c>); o resto é do Admin. O envio de imagem (<c>POST /imagens</c>) confere
/// o Admin e o limite de 30/h ANTES de ler o corpo; lê o multipart em fluxo, para a memória (até 2 MiB), sem arquivo temporário; usa só a parte
/// <c>file</c> e ignora o <c>FileName</c> e o <c>Content-Type</c> que o cliente mandou.
/// </summary>
public sealed class DivulgacoesEndpoints : IEndpointModule
{
    /// <summary>2 MiB do arquivo + 64 KiB de cabeçalhos do multipart (o mesmo valor do <c>client_max_body_size</c> do nginx para a rota).</summary>
    public const long LimiteDoCorpo = LimitesDaImagem.TamanhoMaximoBytes + 64 * 1024;

    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/divulgacoes");

        g.MapGet("/ativas", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "public, max-age=30";
                return Results.Ok(await mediator.Send(new DivulgacoesAtivasQuery(), ct));
            })
            .AllowAnonymous();

        g.MapGet("", async (int? pagina, int? tamanho, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new ListarDivulgacoesQuery(pagina ?? 1, tamanho ?? 20), ct)))
            .RequireAuthorization("SomenteAdmin");

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, IMediator mediator, CancellationToken ct) =>
                ComETag(http, await mediator.Send(new DivulgacaoPorIdQuery(id), ct), StatusCodes.Status200OK))
            .RequireAuthorization("SomenteAdmin");

        g.MapPost("", async (DivulgacaoRequest req, ClaimsPrincipal user, HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                if (!Guid.TryParse(user.FindFirstValue("sub"), out var admin)) return Results.Unauthorized();
                ExigirPrioridadeEAtivo(req);
                var criada = await mediator.Send(new CriarDivulgacaoCommand(admin, req.Titulo ?? "", req.Mensagem ?? "", req.ImagemChave, req.LinkDestino,
                    req.Prioridade!.Value, req.InicioExibicaoUtc, req.FimExibicaoUtc, req.Ativo!.Value), ct);
                http.Response.Headers.Location = $"/api/v1/divulgacoes/{criada.Id}";
                return ComETag(http, criada, StatusCodes.Status201Created);
            })
            .RequireAuthorization("SomenteAdmin");

        g.MapPut("/{id:guid}", async (Guid id, DivulgacaoRequest req, HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                var versao = PreCondicaoIfMatch.Exigir(http, out var faltou);
                if (faltou is not null) return faltou;
                ExigirPrioridadeEAtivo(req);
                return ComETag(http, await mediator.Send(new EditarDivulgacaoCommand(id, versao!, req.Titulo ?? "", req.Mensagem ?? "", req.ImagemChave,
                    req.LinkDestino, req.Prioridade!.Value, req.InicioExibicaoUtc, req.FimExibicaoUtc, req.Ativo!.Value), ct), StatusCodes.Status200OK);
            })
            .RequireAuthorization("SomenteAdmin");

        g.MapDelete("/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new RemoverDivulgacaoCommand(id), ct);
                return Results.NoContent();
            })
            .RequireAuthorization("SomenteAdmin");

        g.MapPost("/imagens", EnviarImagem).RequireAuthorization("SomenteAdmin").DisableAntiforgery();
    }

    /// <summary>
    /// <c>prioridade</c> e <c>ativo</c> são obrigatórios (02 §7) e o <c>PUT</c> substitui o recurso inteiro: completar o campo ausente com um padrão
    /// reativaria em silêncio, no Mural público, uma divulgação desativada. Ausente → 400.
    /// </summary>
    private static void ExigirPrioridadeEAtivo(DivulgacaoRequest req)
    {
        List<FluentValidation.Results.ValidationFailure> faltas = [];
        if (req.Prioridade is null) faltas.Add(new("Prioridade", "A prioridade é obrigatória."));
        if (req.Ativo is null) faltas.Add(new("Ativo", "O campo ativo é obrigatório."));
        if (faltas.Count > 0) throw new FluentValidation.ValidationException(faltas);
    }

    private static async Task<IResult> EnviarImagem(HttpContext http, ClaimsPrincipal user, ILimiteDeUploadDeImagem limite, IMediator mediator, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var admin)) return Results.Unauthorized();

        // 1) O limite de 30/h conta TENTATIVAS e vem antes de ler o corpo: um Admin (ou um token roubado) não faz a Api bufferizar upload em série.
        if (await limite.RegistrarTentativaAsync(admin, ct) is { } espera) throw new LimiteExcedidoException(espera);

        // 2) Tamanho: Content-Length declarado e, para corpo em chunks, o teto do Kestrel.
        if (http.Request.ContentLength > LimiteDoCorpo) return CorpoGrande();
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } teto) teto.MaxRequestBodySize = LimiteDoCorpo;

        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var tipo) || !tipo.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(tipo.Boundary).Value is not { Length: > 0 and <= 200 } fronteira)
            return Invalida("Envie multipart/form-data com a parte \"file\".");

        byte[] bytes;
        try
        {
            bytes = await LerArquivoAsync(http.Request.Body, fronteira, ct);
        }
        catch (InvalidDataException)
        {
            return Invalida("Envie exatamente uma parte \"file\" com a imagem.");
        }
        catch (ArquivoGrandeDemaisException)
        {
            return CorpoGrande();
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return CorpoGrande();
        }

        var enviada = await mediator.Send(new EnviarImagemDeDivulgacaoCommand(bytes), ct);
        return Results.Json(new { chave = enviada.Chave, imagemUrl = enviada.ImagemUrl }, statusCode: StatusCodes.Status201Created);
    }

    private sealed class ArquivoGrandeDemaisException : Exception;

    /// <summary>Exatamente UMA parte, de nome <c>file</c>; o <c>FileName</c> e o <c>Content-Type</c> dela não são lidos. Para ao passar de 2 MiB.</summary>
    private static async Task<byte[]> LerArquivoAsync(System.IO.Stream corpo, string fronteira, CancellationToken ct)
    {
        var leitor = new MultipartReader(fronteira, corpo) { HeadersLengthLimit = 16 * 1024, BodyLengthLimit = LimiteDoCorpo };
        var secao = await leitor.ReadNextSectionAsync(ct) ?? throw new InvalidDataException("sem partes");
        if (!ContentDispositionHeaderValue.TryParse(secao.ContentDisposition, out var disposicao)
            || !disposicao.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(disposicao.Name).Value != "file")
            throw new InvalidDataException("parte que não é file");

        using var memoria = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int lidos;
        while ((lidos = await secao.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (memoria.Length + lidos > LimitesDaImagem.TamanhoMaximoBytes) throw new ArquivoGrandeDemaisException();
            memoria.Write(buffer, 0, lidos);
        }

        if (await leitor.ReadNextSectionAsync(ct) is not null) throw new InvalidDataException("mais de uma parte");
        return memoria.ToArray();
    }

    private static IResult CorpoGrande() => Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, type: "/erros/corpo-grande-demais",
        title: "Corpo grande demais.", detail: $"A imagem pode ter no máximo {LimitesDaImagem.TamanhoMaximoBytes / (1024 * 1024)} MB.");

    private static IResult Invalida(string detalhe) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, type: "/erros/validacao",
        title: "Dados inválidos.", detail: detalhe, extensions: new Dictionary<string, object?> { ["errors"] = new Dictionary<string, string[]> { ["file"] = [detalhe] } });

    private static IResult ComETag(HttpContext http, DivulgacaoDto dto, int status)
    {
        PreCondicaoIfMatch.DefinirETag(http, dto.Versao);
        return Results.Json(dto, statusCode: status);
    }
}
