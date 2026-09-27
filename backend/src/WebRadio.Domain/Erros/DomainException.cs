namespace WebRadio.Domain.Erros;

/// <summary>
/// Base de todo erro de domínio. A tradução para ProblemDetails/HTTP (01-dominio.md §3) é responsabilidade
/// da Api (WebRadio.Api), não do Domain.
/// </summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message) : base(message)
    {
    }
}
