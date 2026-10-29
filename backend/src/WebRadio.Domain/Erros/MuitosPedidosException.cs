namespace WebRadio.Domain.Erros;

/// <summary>
/// Anti-spam do pedido de música (dispositivo, IP, usuário) ou teto global de pedidos pendentes (07, E3-D10). Mapeia para 429
/// <c>/erros/muitos-pedidos</c> com <c>Retry-After</c> e <c>retryAfterSeconds</c>.
/// </summary>
public sealed class MuitosPedidosException(TimeSpan tenteDeNovoEm)
    : DomainException("Muitos pedidos. Tente novamente mais tarde.")
{
    public TimeSpan TenteDeNovoEm { get; } = tenteDeNovoEm;
}
