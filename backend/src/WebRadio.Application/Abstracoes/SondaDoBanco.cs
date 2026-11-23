namespace WebRadio.Application.Abstracoes;

/// <summary>Sonda de saúde do banco (<c>/health/ready</c>): a Api não conhece o EF.</summary>
public interface ISondaDoBanco
{
    Task<bool> ConectaAsync(CancellationToken ct);
}
