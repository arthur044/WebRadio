namespace WebRadio.Domain.Erros;

/// <summary>Recurso inexistente. Mapeia para 404 <c>/erros/nao-encontrado</c>.</summary>
public sealed class NaoEncontradoException(string recurso) : DomainException($"{recurso} não encontrado.");
