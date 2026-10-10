using Microsoft.AspNetCore.Diagnostics;
using WebRadio.Application.Comum;

namespace WebRadio.Api.Erros;

/// <summary>Middleware global de exceções (IExceptionHandler): erro conhecido → ProblemDetails mapeado; o resto → 500 genérico, sem detalhe.</summary>
public sealed class ExcecoesGlobais(IProblemDetailsService problemas, ILogger<ExcecoesGlobais> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext contexto, Exception excecao, CancellationToken cancellationToken)
    {
        // Cliente abortou (ex.: fechou a aba durante o login): nao e erro do servidor, nao gera log de erro nem 500.
        if (excecao is OperationCanceledException && contexto.RequestAborted.IsCancellationRequested)
        {
            contexto.Response.StatusCode = 499;
            return true;
        }

        var problema = ProblemasHttp.Mapear(excecao);

        if (problema is null)
        {
            logger.LogError(excecao, "Exceção não tratada em {Metodo} {Caminho}", contexto.Request.Method, contexto.Request.Path);
            problema = ProblemasHttp.Criar(StatusCodes.Status500InternalServerError, ProblemasHttp.Tipos.Interno, "Erro interno");
        }

        contexto.Response.StatusCode = problema.Status ?? StatusCodes.Status500InternalServerError;

        if (excecao is MuitosPedidosException m)
        {
            contexto.Response.Headers.RetryAfter = ((int)Math.Ceiling(m.TentarNovamenteEm.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return await problemas.TryWriteAsync(new ProblemDetailsContext { HttpContext = contexto, ProblemDetails = problema, Exception = null });
    }
}
