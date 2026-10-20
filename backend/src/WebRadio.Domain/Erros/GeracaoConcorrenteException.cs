namespace WebRadio.Domain.Erros;

/// <summary>Duas gerações de credencial ao mesmo tempo para o mesmo usuário: a que perdeu a corrida no índice único vira 409.</summary>
public sealed class GeracaoConcorrenteException()
    : DomainException("Outra credencial foi gerada ao mesmo tempo. Consulte a credencial ativa ou gere de novo.");
