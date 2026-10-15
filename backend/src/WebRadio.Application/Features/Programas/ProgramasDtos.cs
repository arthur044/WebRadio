using System.Text.Json.Serialization;
using WebRadio.Domain.Grade;

namespace WebRadio.Application.Features.Programas;

public sealed record LocutorDto(Guid Id, string Nome);

/// <summary>
/// 02 §4: <c>{ id, titulo, descricao, locutor: { id, nome }, inicioUtc, fimUtc, status }</c>. <c>Versao</c> é o RowVersion em
/// base64, o mesmo valor do <c>ETag</c>: fica FORA do JSON (o contrato não o tem) e serve só para a API montar o cabeçalho.
/// </summary>
public sealed record ProgramaDto(
    Guid Id,
    string Titulo,
    string? Descricao,
    LocutorDto Locutor,
    DateTime InicioUtc,
    DateTime FimUtc,
    string Status,
    [property: JsonIgnore] string Versao)
{
    public static ProgramaDto De(Programa p, string locutorNome) =>
        new(p.Id, p.Titulo, p.Descricao, new LocutorDto(p.LocutorId, locutorNome), p.InicioUtc, p.FimUtc, p.Status.ToString(),
            Convert.ToBase64String(p.RowVersion));
}
