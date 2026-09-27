namespace WebRadio.Domain.Erros;

/// <summary>
/// Tentativa de aprovar/rejeitar um PedidoMusica que já saiu de Pendente. Mapeia para 409 (01-dominio.md §3).
/// </summary>
public sealed class PedidoJaModeradoException : DomainException
{
    public Guid PedidoId { get; }

    public PedidoJaModeradoException(Guid pedidoId)
        : base($"O pedido {pedidoId} já foi moderado.")
    {
        PedidoId = pedidoId;
    }
}
