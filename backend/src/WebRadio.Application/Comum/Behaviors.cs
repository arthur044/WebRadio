using System.Diagnostics;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace WebRadio.Application.Comum;

/// <summary>Pipeline MediatR: Logging → Validation → handler (E1-F06). Só registra o nome do request: o corpo pode ter senha.</summary>
public sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var nome = typeof(TRequest).Name;
        var inicio = Stopwatch.GetTimestamp();

        try
        {
            var resposta = await next();
            logger.LogDebug("{Request} concluído em {ElapsedMs} ms", nome, Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
            return resposta;
        }
        catch (Exception ex) when (ex is ValidationException or CredenciaisInvalidasException or NaoEncontradoException)
        {
            // Erros esperados do cliente: nível baixo, sem stack.
            logger.LogInformation("{Request} recusado: {Tipo}", nome, ex.GetType().Name);
            throw;
        }
    }
}

public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var lista = validators.ToList();
        if (lista.Count > 0)
        {
            var contexto = new ValidationContext<TRequest>(request);
            var resultados = await Task.WhenAll(lista.Select(v => v.ValidateAsync(contexto, cancellationToken)));
            var falhas = resultados.SelectMany(r => r.Errors).Where(f => f is not null).ToList();

            if (falhas.Count > 0)
            {
                throw new ValidationException(falhas);
            }
        }

        return await next();
    }
}
