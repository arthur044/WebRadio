using FluentValidation;
using MediatR;

namespace WebRadio.Application.Pipeline;

/// <summary>Roda todos os IValidator&lt;TRequest&gt; antes do handler; falhas viram ValidationException (→ 400 na Api).</summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validadores)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (validadores.Any())
        {
            var contexto = new ValidationContext<TRequest>(request);
            var resultados = await Task.WhenAll(validadores.Select(v => v.ValidateAsync(contexto, cancellationToken)));
            var falhas = resultados.SelectMany(r => r.Errors).Where(f => f is not null).ToList();
            if (falhas.Count > 0)
                throw new ValidationException(falhas);
        }

        return await next();
    }
}
