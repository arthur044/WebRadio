namespace WebRadio.Domain.Enums;

/// <summary>Valores fixos (01-dominio.md §1): nunca renumerar.</summary>
public enum StatusSanitizacao
{
    AguardandoUpload = 1,
    Pendente = 2,
    EmAnalise = 3,
    Aprovado = 4,
    Quarentena = 5,
    Rejeitado = 6,
}
