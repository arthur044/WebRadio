namespace WebRadio.Domain.Erros;

/// <summary>
/// Uma transição de máquina de estado foi tentada a partir de um estado que não a permite. Mapeia para 409
/// (01-dominio.md §3).
/// </summary>
public sealed class TransicaoInvalidaException : DomainException
{
    public TransicaoInvalidaException(string message) : base(message)
    {
    }
}
