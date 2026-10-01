using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace WebRadio.Application.Pipeline;

/// <summary>
/// Primeiro da fila (Logging → Validation). Registra só o NOME do request e a duração: nunca o conteúdo,
/// que pode ter e-mail, senha ou token (05-seguranca-auditoria.md §5.3, sem PII em log).
/// </summary>
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
            logger.LogInformation("Request {Request} concluído em {ElapsedMs} ms", nome, Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
            return resposta;
        }
        catch (Exception ex)
        {
            // Só o tipo da exceção: a mensagem pode carregar dado do usuário.
            logger.LogWarning("Request {Request} falhou com {Excecao} em {ElapsedMs} ms", nome, ex.GetType().Name, Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
            throw;
        }
    }
}
