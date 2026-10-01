using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using WebRadio.Domain.Erros;

namespace WebRadio.Api.Infra;

/// <summary>Mapeia exceções para ProblemDetails (01-dominio.md §3). Exceção inesperada → 500 genérico, sem mensagem.</summary>
public sealed class TratadorDeExcecoes(ILogger<TratadorDeExcecoes> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        // Cliente desistiu: não é erro do servidor nem merece LogError.
        if (ex is OperationCanceledException && http.RequestAborted.IsCancellationRequested)
        {
            http.Response.StatusCode = 499;
            return true;
        }

        var problema = ex switch
        {
            ValidationException v => Validacao(v),
            ConflitoDeGradeException c => Criar(409, "/erros/conflito-grade", "Conflito de horário na grade.", c.Message,
                ("programaConflitanteId", c.ProgramaConflitanteId)),
            PedidoJaModeradoException p => Criar(409, "/erros/transicao-invalida", "Transição inválida.", p.Message),
            TransicaoInvalidaException t => Criar(409, "/erros/transicao-invalida", "Transição inválida.", t.Message),
            _ => null,
        };

        if (problema is null)
        {
            // Nada vindo do cliente no log (CodeQL: log forging): nem path nem método. O padrão da rota é do
            // código, não da requisição; a stack vai para o log estruturado, não para o cliente.
            var rota = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(sem rota)";
            logger.LogError(ex, "Exceção não tratada na rota {Rota}", rota);
            problema = Criar(500, "/erros/interno", "Erro interno.", "Ocorreu um erro inesperado.",
                ("traceId", System.Diagnostics.Activity.Current?.Id ?? http.TraceIdentifier));
        }

        http.Response.StatusCode = problema.Status!.Value;
        await http.Response.WriteAsJsonAsync(problema, options: null, contentType: "application/problem+json", ct);
        return true;
    }

    private static ProblemDetails Criar(int status, string type, string titulo, string detalhe, params (string Chave, object Valor)[] extras)
    {
        var p = new ProblemDetails { Status = status, Type = type, Title = titulo, Detail = detalhe };
        foreach (var (chave, valor) in extras) p.Extensions[chave] = valor;
        return p;
    }

    private static ProblemDetails Validacao(ValidationException v)
    {
        var p = Criar(400, "/erros/validacao", "Dados inválidos.", "Um ou mais campos são inválidos.");
        p.Extensions["errors"] = v.Errors.GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
        return p;
    }
}
