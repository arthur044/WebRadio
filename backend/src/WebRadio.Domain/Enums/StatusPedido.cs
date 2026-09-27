namespace WebRadio.Domain.Enums;

/// <summary>Valores fixos (01-dominio.md §1): nunca renumerar.</summary>
public enum StatusPedido
{
    Pendente = 1,
    Aprovado = 2,
    Rejeitado = 3,
    Tocado = 4,
    Expirado = 5,
}
