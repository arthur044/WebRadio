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
            // Sequencial de propósito: um validador que consulta o DbContext (scoped) estoura com
            // "a second operation was started" se rodar em paralelo no mesmo contexto.
            var falhas = new List<FluentValidation.Results.ValidationFailure>();
            foreach (var validador in validadores)
            {
                var resultado = await validador.ValidateAsync(contexto, cancellationToken);
                falhas.AddRange(resultado.Errors.Where(f => f is not null));
            }

            if (falhas.Count > 0)
                throw new ValidationException(falhas);
        }

        return await next();
    }
}
