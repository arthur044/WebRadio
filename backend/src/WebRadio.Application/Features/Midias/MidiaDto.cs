using System.Text.Json.Serialization;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Midia;

namespace WebRadio.Application.Features.Midias;

/// <summary>
/// 02 §6: <c>{ id, nomeOriginal, titulo, artista, tipoMidia, mimeType, tamanhoBytes, duracaoSegundos, statusSanitizacao, motivoRejeicao, dataUploadUtc,
/// ativo, enviadoPorNome }</c>. Os hashes saem em hex só para o Admin (nulos para o Locutor). <c>Versao</c> (RowVersion em base64, o valor do <c>ETag</c>)
/// fica FORA do JSON.
/// </summary>
public sealed record MidiaDto(
    Guid Id,
    string NomeOriginal,
    string? Titulo,
    string? Artista,
    TipoMidia TipoMidia,
    string? MimeType,
    long TamanhoBytes,
    decimal? DuracaoSegundos,
    StatusSanitizacao StatusSanitizacao,
    string? MotivoRejeicao,
    DateTime DataUploadUtc,
    bool Ativo,
    string? EnviadoPorNome,
    string? HashSha256,
    string? HashOriginalSha256,
    [property: JsonIgnore] string Versao)
{
    // Hashes, nome do arquivo e do autor não vão para o log com {Obj}.
    public override string ToString() => nameof(MidiaDto);

    public static MidiaDto De(ArquivoMidia m, string? enviadoPorNome, bool admin) =>
        new(m.Id, m.NomeOriginal, m.Titulo, m.Artista, m.TipoMidia, m.MimeType, m.TamanhoBytes, m.DuracaoSegundos, m.StatusSanitizacao, m.MotivoRejeicao,
            m.DataUploadUtc, m.Ativo, enviadoPorNome,
            admin && m.HashSHA256 is { } h ? Convert.ToHexStringLower(h) : null,
            admin && m.HashOriginalSHA256 is { } ho ? Convert.ToHexStringLower(ho) : null,
            Convert.ToBase64String(m.RowVersion));
}

/// <summary><c>GET /midias/{id}/preview?formato=url</c> (08 E4-F03): a mesma URL pré-assinada do <c>302</c>, em JSON, para o <c>&lt;audio src&gt;</c> (que não manda Bearer).</summary>
public sealed record PreviewDto(string Url, DateTime ExpiraEmUtc);
