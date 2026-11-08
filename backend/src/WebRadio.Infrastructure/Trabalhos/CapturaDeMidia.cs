using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Trabalhos;

/// <summary>
/// Captura da fila de sanitização (08 E4-F05, <c>00</c> Fluxo C passo 3). No SQL Server é UM comando: a CTE <c>SELECT TOP (1) ... WITH (UPDLOCK, READPAST, ROWLOCK) ORDER BY DataUploadUtc</c>
/// com o <c>UPDATE</c> sobre ela e <c>OUTPUT</c> (<c>EmAnalise</c>, tentativa + 1, lease = agora do SQL): dois Workers nunca pegam o mesmo item (o segundo PULA a linha travada em vez de
/// esperar). O filtro <c>StatusSanitizacao = 2</c> é CONSTANTE. No provedor em memória (testes de unidade, sem transação) usa o domínio.
/// </summary>
public sealed class CapturaDeMidia(RadioDbContext db, IClock clock) : ICapturaDeMidia
{
    private static readonly byte Pendente = (byte)StatusSanitizacao.Pendente;
    private static readonly byte EmAnalise = (byte)StatusSanitizacao.EmAnalise;

    private static readonly string Sql = $"""
        SET XACT_ABORT ON;
        DECLARE @capturado TABLE (Id uniqueidentifier);

        ;WITH alvo AS (
            SELECT TOP (1) *
            FROM midia.ArquivoMidia WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE StatusSanitizacao = {Pendente}
            ORDER BY DataUploadUtc
        )
        UPDATE alvo SET StatusSanitizacao = {EmAnalise}, TentativasSanitizacao = TentativasSanitizacao + 1, EmAnaliseDesdeUtc = SYSUTCDATETIME()
        OUTPUT inserted.Id INTO @capturado;

        SELECT Id AS [Value] FROM @capturado;
        """;

    public async Task<Guid?> CapturarProximaAsync(CancellationToken ct = default)
    {
        if (db.Database.IsSqlServer())
        {
            var ids = await db.Database.SqlQueryRaw<Guid>(Sql).ToListAsync(ct);
            return ids.Count == 0 ? null : ids[0];
        }

        var midia = await db.ArquivosMidia.Where(m => m.StatusSanitizacao == StatusSanitizacao.Pendente).OrderBy(m => m.DataUploadUtc).FirstOrDefaultAsync(ct);
        if (midia is null) return null;
        midia.IniciarAnalise(clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return midia.Id;
    }
}
