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
        new(p.Id, p.Titulo, p.Descricao, new LocutorDto(p.LocutorId, locutorNome), EmUtc(p.InicioUtc), EmUtc(p.FimUtc), p.Status.ToString(),
            Convert.ToBase64String(p.RowVersion));

    /// <summary>
    /// O EF materializa <c>datetime2</c> como <c>Unspecified</c>, e o JSON de um <c>Unspecified</c> sai SEM o sufixo Z: o navegador
    /// leria a hora como local. Tudo no banco é UTC (02 §4), então o Kind é marcado aqui, na saída.
    /// </summary>
    private static DateTime EmUtc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
