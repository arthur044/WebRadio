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

    /// <summary>Espera da trava quando quem chama não define uma (a sobrecarga sem <c>espera</c>). Configurável para o teste não esperar 15 s.</summary>
    public TimeSpan EsperaPadrao { get; init; } = TimeSpan.FromMilliseconds(TimeoutDaTravaMs);

    public Task<T> ExecutarAsync<T>(string recurso, Func<CancellationToken, Task<T>> acao, CancellationToken ct) =>
        ExecutarAsync([recurso], EsperaPadrao, acao, ct);

    public Task<T> ExecutarAsync<T>(string recurso, TimeSpan espera, Func<CancellationToken, Task<T>> acao, CancellationToken ct) =>
        ExecutarAsync([recurso], espera, acao, ct);

    public async Task<T> ExecutarAsync<T>(IReadOnlyList<string> recursos, TimeSpan espera, Func<CancellationToken, Task<T>> acao, CancellationToken ct)
    {
        // Ordem ordinal fixa: sem ela, A pede (x, y) e B pede (y, x) e as duas esperariam uma à outra.
        var ordenados = recursos.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        try
        {
            // Só os testes HTTP usam o provedor em memória, que não tem transação nem applock; lá não há concorrência.
            if (!db.Database.IsSqlServer()) return await acao(ct);

            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async ct2 =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct2);
                foreach (var recurso in ordenados)
                    await AdquirirAsync(db, recurso, (int)espera.TotalMilliseconds, ct2);
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
        AdquirirAsync(db, recurso, TimeoutDaTravaMs, ct);

    /// <summary>
    /// <c>sp_getapplock</c> devolve -1 no estouro da espera, -2 se cancelado e -3 se vítima de deadlock. O -1 vira SEMPRE <see cref="TravaOcupadaException"/> (quem pediu uma espera curta a trata;
    /// quem não trata deixa subir e a Api responde 503 + Retry-After, nunca 500, #25); o -2 é o cancelamento de quem chamou; o -3 (e qualquer outro código negativo) vira
    /// <see cref="ServidorOcupadoException"/>, também 503: tentar de novo resolve.
    /// </summary>
    private static async Task AdquirirAsync(RadioDbContext db, string recurso, int esperaMs, CancellationToken ct)
    {
        var resultado = (await db.Database.SqlQuery<int>(
            $"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {recurso}, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = {esperaMs}; SELECT @r AS [Value];")
            .ToListAsync(ct)).Single();
        if (resultado >= 0) return;
        if (resultado == -1) throw new TravaOcupadaException();
        ct.ThrowIfCancellationRequested();
        throw new ServidorOcupadoException();
    }
}

/// <summary>Sonda do <c>/health/ready</c>: só abre uma conexão; não é responsabilidade da transação com trava.</summary>
public sealed class SondaDoBanco(RadioDbContext db) : ISondaDoBanco
{
    public Task<bool> ConectaAsync(CancellationToken ct) => db.Database.CanConnectAsync(ct);
}
