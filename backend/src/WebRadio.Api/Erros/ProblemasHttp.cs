using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using WebRadio.Application.Comum;
using WebRadio.Domain.Erros;

namespace WebRadio.Api.Erros;

/// <summary>01-dominio.md §3: tradução de erros de domínio/aplicação para ProblemDetails.</summary>
public static class ProblemasHttp
{
    public static class Tipos
    {
        public const string Validacao = "/erros/validacao";
        public const string Credenciais = "/erros/credenciais";
        public const string RefreshConcorrente = "/erros/refresh-concorrente";
        public const string TrocaDeSenhaObrigatoria = "/erros/troca-de-senha-obrigatoria";
        public const string Proibido = "/erros/proibido";
        public const string NaoAutenticado = "/erros/nao-autenticado";
        public const string NaoEncontrado = "/erros/nao-encontrado";
        public const string ConflitoGrade = "/erros/conflito-grade";
        public const string TransicaoInvalida = "/erros/transicao-invalida";
        public const string EmailJaCadastrado = "/erros/email-ja-cadastrado";
        public const string VersaoDesatualizada = "/erros/versao-desatualizada";
        public const string MuitosPedidos = "/erros/muitos-pedidos";
        public const string LimiteDeRequisicoes = "/erros/limite-de-requisicoes";
        public const string Interno = "/erros/interno";
    }

    public static ProblemDetails Criar(int status, string tipo, string titulo, string? detalhe = null) =>
        new() { Status = status, Type = tipo, Title = titulo, Detail = detalhe };

    public static ProblemDetails Credenciais() =>
        Criar(StatusCodes.Status401Unauthorized, Tipos.Credenciais, "Credenciais inválidas", "E-mail ou senha inválidos.");

    /// <summary>Mapeia a exceção; devolve null para o que não é erro conhecido (vira 500 sem vazar detalhe).</summary>
    public static ProblemDetails? Mapear(Exception ex) => ex switch
    {
        ValidationException v => Validacao(v),
        CredenciaisInvalidasException => Credenciais(),
        NaoEncontradoException n => Criar(StatusCodes.Status404NotFound, Tipos.NaoEncontrado, "Não encontrado", n.Message),
        EmailJaCadastradoException e => Criar(StatusCodes.Status409Conflict, Tipos.EmailJaCadastrado, "Conflito", e.Message),
        ConflitoDeGradeException c => ConflitoGrade(c),
        TransicaoInvalidaException or PedidoJaModeradoException =>
            Criar(StatusCodes.Status409Conflict, Tipos.TransicaoInvalida, "Transição inválida", ex.Message),
        VersaoDesatualizadaException => Criar(StatusCodes.Status412PreconditionFailed, Tipos.VersaoDesatualizada, "Versão desatualizada", ex.Message),
        MuitosPedidosException m => MuitosPedidos(m),
        BadHttpRequestException b => Criar(b.StatusCode is >= 400 and < 500 ? b.StatusCode : StatusCodes.Status400BadRequest, Tipos.Validacao, "Requisição inválida", "A requisição está malformada ou excede os limites aceitos."),
        _ => null,
    };

    private static ProblemDetails Validacao(ValidationException v)
    {
        var problema = Criar(StatusCodes.Status400BadRequest, Tipos.Validacao, "Erro de validação", "Um ou mais campos são inválidos.");
        var erros = v.Errors
            .GroupBy(e => CamelCase(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());

        problema.Extensions["errors"] = erros;
        return problema;
    }

    private static ProblemDetails ConflitoGrade(ConflitoDeGradeException c)
    {
        var problema = Criar(StatusCodes.Status409Conflict, Tipos.ConflitoGrade, "Conflito de grade", c.Message);
        problema.Extensions["programaConflitanteId"] = c.ProgramaConflitanteId;
        return problema;
    }

    private static ProblemDetails MuitosPedidos(MuitosPedidosException m)
    {
        var problema = Criar(StatusCodes.Status429TooManyRequests, Tipos.MuitosPedidos, "Muitos pedidos", m.Message);
        problema.Extensions["retryAfterSeconds"] = (int)Math.Ceiling(m.TentarNovamenteEm.TotalSeconds);
        return problema;
    }

    private static string CamelCase(string propriedade) =>
        string.IsNullOrEmpty(propriedade) ? "_" : char.ToLowerInvariant(propriedade[0]) + propriedade[1..];
}
