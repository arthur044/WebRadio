namespace WebRadio.Domain.Erros;

/// <summary>O <c>sp_getapplock</c> não saiu dentro da espera pedida. Quem pediu uma espera curta decide a resposta (ex.: 429 com Retry-After de 1 s).</summary>
public sealed class TravaOcupadaException() : DomainException("Recurso ocupado. Tente novamente em instantes.");
