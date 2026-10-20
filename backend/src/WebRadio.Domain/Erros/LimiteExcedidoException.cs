namespace WebRadio.Domain.Erros;

/// <summary>Limite de tentativas por usuário (ex.: 5 gerações de credencial de transmissão por hora). Mapeia para 429 com Retry-After.</summary>
public sealed class LimiteExcedidoException(TimeSpan tenteDeNovoEm)
    : DomainException("Limite de tentativas atingido. Tente novamente mais tarde.")
{
    public TimeSpan TenteDeNovoEm { get; } = tenteDeNovoEm;
}
