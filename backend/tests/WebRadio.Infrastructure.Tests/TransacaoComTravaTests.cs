using Microsoft.EntityFrameworkCore;
using WebRadio.Domain.Erros;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>#25: o estouro da espera da trava (sp_getapplock) nunca vira erro de SQL/500: é <see cref="TravaOcupadaException"/> também na espera PADRÃO (a Api responde 503 + Retry-After).</summary>
public class TransacaoComTravaTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private const string Recurso = "teste:f04:trava-ocupada";

    /// <summary>Outra conexão segura a trava (transação aberta) até o <c>await using</c> terminar.</summary>
    private async Task<IAsyncDisposable> SegurarAsync(string recurso)
    {
        var db = banco.CriarContexto();
        var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {recurso}, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;");
        return new Seguranca(db, tx);
    }

    private sealed class Seguranca(RadioDbContext db, IAsyncDisposable tx) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await tx.DisposeAsync();
            await db.DisposeAsync();
        }
    }

    [SqlFact]
    public async Task Espera_padrao_estourada_lanca_TravaOcupada_e_nao_erro_de_sql()
    {
        await using var _ = await SegurarAsync(Recurso);
        await using var db = banco.CriarContexto();
        var trava = new TransacaoComTrava(db) { EsperaPadrao = TimeSpan.FromMilliseconds(300) };

        await Assert.ThrowsAsync<TravaOcupadaException>(() => trava.ExecutarAsync(Recurso, _ => Task.FromResult(1), default));
    }

    [SqlFact]
    public async Task Espera_curta_explicita_estourada_tambem_lanca_TravaOcupada()
    {
        await using var _ = await SegurarAsync(Recurso + ":curta");
        await using var db = banco.CriarContexto();

        await Assert.ThrowsAsync<TravaOcupadaException>(() => new TransacaoComTrava(db).ExecutarAsync(Recurso + ":curta", TimeSpan.FromMilliseconds(200), _ => Task.FromResult(1), default));
    }

    [SqlFact]
    public async Task Trava_livre_executa_o_bloco_e_solta_no_commit()
    {
        await using var db = banco.CriarContexto();
        var trava = new TransacaoComTrava(db) { EsperaPadrao = TimeSpan.FromMilliseconds(300) };

        Assert.Equal(7, await trava.ExecutarAsync(Recurso + ":livre", _ => Task.FromResult(7), default));
        // Solta no commit: outra execução logo depois não espera.
        Assert.Equal(8, await trava.ExecutarAsync(Recurso + ":livre", _ => Task.FromResult(8), default));
    }
}
