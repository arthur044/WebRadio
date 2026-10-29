using System.Text.Json.Serialization;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Interacao;

namespace WebRadio.Application.Features.Divulgacoes;

/// <summary>
/// 02 §7: <c>{ id, titulo, mensagem, imagemChave, imagemUrl, linkDestino, prioridade, inicioExibicaoUtc, fimExibicaoUtc, ativo }</c>.
/// <c>imagemUrl</c> é a URL pública derivada de <c>imagemChave</c>; as duas são nulas sem imagem. <c>Versao</c> (RowVersion em base64, o valor
/// do <c>ETag</c>) fica FORA do JSON.
/// </summary>
public sealed record DivulgacaoDto(
    Guid Id,
    string Titulo,
    string Mensagem,
    string? ImagemChave,
    string? ImagemUrl,
    string? LinkDestino,
    byte Prioridade,
    DateTime? InicioExibicaoUtc,
    DateTime? FimExibicaoUtc,
    bool Ativo,
    [property: JsonIgnore] string Versao)
{
    public static DivulgacaoDto De(Divulgacao d, IObjectStorage? storage) =>
        new(d.Id, d.Titulo, d.Mensagem, d.ImagemChaveStorage, UrlDaImagem(d.ImagemChaveStorage, storage), d.LinkDestino, d.Prioridade,
            d.InicioExibicaoUtc, d.FimExibicaoUtc, d.Ativo, Convert.ToBase64String(d.RowVersion));

    public static string? UrlDaImagem(string? chave, IObjectStorage? storage) =>
        chave is null || storage is null ? null : storage.UrlPublica(Buckets.Publico, chave);
}
