using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Storage;

namespace WebRadio.Infrastructure.Persistencia;

/// <summary>
/// #25: quando o erro vem de o BANCO estar fora ou ocupado demais (a conexão caiu, o servidor não responde, deadlock), a Api responde 503 + Retry-After e não 500. A decisão mora aqui porque a Api
/// não pode conhecer o EF Core (regra de arquitetura): ela só pergunta.
/// </summary>
/// <remarks>
/// NÃO é "qualquer exceção de banco": uma <see cref="SqlException"/> de permissão (229, um GRANT que faltou no deploy), de objeto inexistente (207, 208), de restrição (547, 2627), de
/// conversão ou um RAISERROR/THROW de procedure é BUG ou configuração, e continua 500 com o erro completo no log. Só o que "tentar de novo" resolve vira 503.
/// </remarks>
public static class FalhaDeBanco
{
    /// <summary>
    /// Números de erro do SQL Server que são indisponibilidade ou conectividade: -2 (tempo esgotado), 53 (caminho de rede não achado), 64 (conexão derrubada no login), 233 (ninguém do outro lado),
    /// 4060 e 40613 (o banco não abre: subindo, recuperando ou indisponível), 10053, 10054, 10060, 10061 (soquete: abortada, resetada, tempo esgotado, recusada), 11001 (host não achado) e
    /// 1205 e 1222 (vítima de deadlock e tempo esgotado de trava: tentar de novo resolve). O 18456 (login recusado) NÃO está aqui: é credencial errada, erro de configuração.
    /// </summary>
    private static readonly HashSet<int> NumerosDeIndisponibilidade = [-2, 53, 64, 233, 4060, 40613, 10053, 10054, 10060, 10061, 11001, 1205, 1222];

    public static bool EhNumeroDeIndisponibilidade(int numeroDoErro) => NumerosDeIndisponibilidade.Contains(numeroDoErro);

    /// <summary>
    /// O <see cref="RetryLimitExceededException"/> do EF (esgotou as tentativas de reconexão: só falha TRANSITÓRIA chega até ele) ou uma <see cref="SqlException"/> com número de indisponibilidade.
    /// Um <c>DbUpdateException</c> (violação de restrição, conflito) e qualquer outra exceção NÃO são indisponibilidade.
    /// </summary>
    public static bool Indisponivel(Exception ex) =>
        ex is RetryLimitExceededException || (ex is SqlException sql && EhNumeroDeIndisponibilidade(sql.Number));

    /// <summary>O número do erro do SQL Server por trás da exceção (ela própria ou a interna do <see cref="RetryLimitExceededException"/>), para o log. <c>null</c> se não houver.</summary>
    public static int? Numero(Exception ex) => (ex as SqlException ?? ex.InnerException as SqlException)?.Number;
}
