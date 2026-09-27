namespace WebRadio.Domain.Erros;

/// <summary>
/// Um novo horário de Programa cruza o intervalo de outro programa Agendado/AoVivo. A checagem em si roda na
/// Application (D7: sp_getapplock + consulta de interseção), mas o tipo de erro é do Domain. Mapeia para 409
/// (01-dominio.md §3).
/// </summary>
public sealed class ConflitoDeGradeException : DomainException
{
    public Guid ProgramaConflitanteId { get; }

    public ConflitoDeGradeException(Guid programaConflitanteId)
        : base($"O horário conflita com o programa {programaConflitanteId}.")
    {
        ProgramaConflitanteId = programaConflitanteId;
    }
}
