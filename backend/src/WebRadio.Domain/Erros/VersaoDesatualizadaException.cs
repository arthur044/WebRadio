namespace WebRadio.Domain.Erros;

/// <summary>O <c>If-Match</c> não bate com o RowVersion atual: outra pessoa alterou o recurso. Mapeia para 412.</summary>
public sealed class VersaoDesatualizadaException() : DomainException("O registro foi alterado por outra pessoa. Recarregue e tente de novo.");
