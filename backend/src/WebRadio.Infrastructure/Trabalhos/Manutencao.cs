using Microsoft.EntityFrameworkCore;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Trabalhos;

public sealed record ResultadoDaManutencao(int PedidosAnonimizados, int EventosApagados, int RefreshApagados);

public interface IManutencao
{
    Task<ResultadoDaManutencao> ExecutarAsync(CancellationToken ct);
}

/// <summary>
/// Rotinas da seção 11 do <c>03-schema-sqlserver.sql</c>, em lotes de 4.000 (abaixo do limiar de ~5.000 locks que escala o lock para a tabela; o DELETE toca o clustered e os índices).
/// O "agora" é o do SQL (<c>SYSUTCDATETIME()</c>), não o do contêiner. Tudo é idempotente: rodar duas vezes, ou em
/// dois workers ao mesmo tempo, não muda o resultado (a liderança só evita trabalho dobrado).
/// </summary>
public sealed class Manutencao(RadioDbContext db) : IManutencao
{
    public const int LotePadrao = 4000;

    public int Lote { get; init; } = LotePadrao;

    public async Task<ResultadoDaManutencao> ExecutarAsync(CancellationToken ct)
    {
        // LGPD: apaga os hashes do ouvinte depois de 30 dias; o pedido em si fica.
        var anonimizados = await EmLotes(() => db.Database.ExecuteSqlAsync($"""
            UPDATE TOP ({Lote}) interacao.PedidoMusica SET ListenerDeviceHash = NULL, ListenerIpHash = NULL
            WHERE CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME()) AND (ListenerDeviceHash IS NOT NULL OR ListenerIpHash IS NOT NULL);
            """, ct));

        // Só o que já foi entregue: evento parado (ProcessadoEmUtc nulo) fica para análise (01 §2.8).
        var eventos = await EmLotes(() => db.Database.ExecuteSqlAsync($"""
            DELETE TOP ({Lote}) FROM infra.EventoOutbox WHERE ProcessadoEmUtc < DATEADD(DAY, -7, SYSUTCDATETIME());
            """, ct));

        // FK_RefreshToken_SubstituidoPor é RESTRICT e o lote não tem ordem: um sucessor apagado antes do antecessor
        // violaria a FK. Primeiro os vencidos soltam o ponteiro; depois sai quem ninguém mais aponta. Um vencido ainda
        // apontado por token que NÃO venceu (duração do refresh mudou entre emissões) fica para outro dia.
        await EmLotes(() => db.Database.ExecuteSqlAsync($"""
            UPDATE TOP ({Lote}) seg.RefreshToken SET SubstituidoPorId = NULL
            WHERE SubstituidoPorId IS NOT NULL AND ExpiraEmUtc < DATEADD(DAY, -1, SYSUTCDATETIME());
            """, ct));
        var refresh = await EmLotes(() => db.Database.ExecuteSqlAsync($"""
            DELETE TOP ({Lote}) t FROM seg.RefreshToken AS t
            WHERE t.ExpiraEmUtc < DATEADD(DAY, -1, SYSUTCDATETIME())
              AND NOT EXISTS (SELECT 1 FROM seg.RefreshToken AS a WHERE a.SubstituidoPorId = t.Id);
            """, ct));

        return new ResultadoDaManutencao(anonimizados, eventos, refresh);
    }

    private async Task<int> EmLotes(Func<Task<int>> passo)
    {
        var total = 0;
        int n;
        do
        {
            n = await passo();
            total += n;
        } while (n == Lote);
        return total;
    }
}
