using System.Data.Common;
using Microsoft.EntityFrameworkCore.Storage;

namespace WebRadio.Infrastructure.Persistencia;

/// <summary>
/// #25: quando o erro vem de o BANCO estar fora (a conexão caiu, o login falhou, o servidor não responde), a Api responde 503 + Retry-After e não 500. A decisão mora aqui porque a Api não
/// pode conhecer o EF Core (regra de arquitetura): ela só pergunta.
/// </summary>
public static class FalhaDeBanco
{
    /// <summary>
    /// <see cref="DbException"/> direta (a <c>SqlException</c> de uma conexão que falhou) ou o <see cref="RetryLimitExceededException"/> do EF (esgotou as tentativas de reconexão).
    /// Um <c>DbUpdateException</c> (violação de restrição, conflito) NÃO é indisponibilidade: continua erro de verdade.
    /// </summary>
    public static bool Indisponivel(Exception ex) => ex is DbException or RetryLimitExceededException;
}
