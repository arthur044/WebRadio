using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Erros;

namespace WebRadio.Infrastructure.Persistencia;

/// <summary>
/// Transação + <c>sp_getapplock</c> exclusivo (dono = a transação: some no commit/rollback), dentro da execution strategy
/// do EF (com EnableRetryOnFailure, transação manual só é aceita assim). No retry o bloco inteiro roda de novo, com o
/// ChangeTracker limpo.
/// </summary>
public sealed class TransacaoComTrava(RadioDbContext db) : ITransacaoComTrava
{
    public const int TimeoutDaTravaMs = 15_000;

    public async Task<T> ExecutarAsync<T>(string recurso, Func<CancellationToken, Task<T>> acao, CancellationToken ct)
    {
        try
        {
            // Só os testes HTTP usam o provedor em memória, que não tem transação nem applock; lá não há concorrência.
            if (!db.Database.IsSqlServer()) return await acao(ct);

            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async ct2 =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct2);
                await AdquirirAsync(db, recurso, ct2);
                var resultado = await acao(ct2);
                await tx.CommitAsync(ct2);
                return resultado;
            }, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Alguém fora da trava (ex.: a própria pessoa trocando a senha) alterou a linha entre a leitura e o UPDATE:
            // o RowVersion lido não vale mais. Mesmo erro de um If-Match divergente (412).
            throw new VersaoDesatualizadaException();
        }
    }

    /// <summary>Exige uma transação aberta em <paramref name="db"/>. O recurso vai como parâmetro, nunca concatenado.</summary>
    internal static Task AdquirirAsync(RadioDbContext db, string recurso, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {recurso}, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = {TimeoutDaTravaMs}; IF @r < 0 THROW 50001, N'applock indisponível', 1;",
            ct);
}

/// <summary>Sonda do <c>/health/ready</c>: só abre uma conexão; não é responsabilidade da transação com trava.</summary>
public sealed class SondaDoBanco(RadioDbContext db) : ISondaDoBanco
{
    public Task<bool> ConectaAsync(CancellationToken ct) => db.Database.CanConnectAsync(ct);
}
