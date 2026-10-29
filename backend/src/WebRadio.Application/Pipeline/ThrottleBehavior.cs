using MediatR;

namespace WebRadio.Application.Pipeline;

/// <summary>
/// Request com limite de frequência (Fluxo B: <c>Logging → Validation → Throttle → Handler</c>). A regra de cada request fica num
/// <see cref="IThrottleDoRequest{TRequest}"/>; o pipeline só a chama, depois da validação (entrada inválida não gasta limite).
/// </summary>
public interface IThrottledRequest : IBaseRequest;

public interface IThrottleDoRequest<in TRequest> where TRequest : IThrottledRequest
{
    /// <summary>Lança a exceção de domínio do limite (429) quando o request não pode passar; senão reserva a vaga.</summary>
    Task AvaliarAsync(TRequest request, CancellationToken ct);
}

public sealed class ThrottleBehavior<TRequest, TResponse>(IEnumerable<IThrottleDoRequest<TRequest>> regras)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull, IThrottledRequest
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        foreach (var regra in regras)
            await regra.AvaliarAsync(request, cancellationToken);
        return await next();
    }
}
