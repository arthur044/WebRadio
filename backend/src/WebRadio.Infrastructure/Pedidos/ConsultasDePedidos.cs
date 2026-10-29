using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Grade;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Pedidos;

/// <summary>
/// SQL explícito do pedido de música (E3-F01): a hora é a do servidor (<c>SYSUTCDATETIME()</c>), nunca a do <c>IClock</c>, e o formato de
/// cada consulta é o que o Atlas mede no E3-A01 (1, 4, 7 e 9). Os status são CONSTANTES no texto (<c>Status = 1</c>): com parâmetro o SQL
/// Server não usa índice filtrado.
/// </summary>
public sealed class ConsultasDePedidos(RadioDbContext db) : IConsultasDePedidos
{
    /// <summary>Janela do anti-spam, em segundos (a mesma de <c>ThrottleDePedido.Janela</c>).</summary>
    private const int JanelaEmSegundos = 300;

    public async Task<Guid?> ProgramaDaJanelaAsync(CancellationToken ct)
    {
        // Status NOT IN (3, 4) = Finalizado, Cancelado (a regra do harbor, 06 "Autorização do ao vivo"; não depende do Status do Worker).
        // O limite da duração máxima em InicioUtc mantém um seek de faixa no IX_Programa_Grade (parecer A02 §4). Dois locutores podem ter
        // programas simultâneos (D7 só impede o mesmo locutor); vale o de início mais recente.
        var duracaoMaximaEmMinutos = (int)Programa.DuracaoMaxima.TotalMinutes;
        var linhas = await db.Database.SqlQuery<Guid>($"""
            SELECT TOP (1) p.Id AS [Value]
            FROM grade.Programa AS p
            WHERE p.InicioUtc <= SYSUTCDATETIME()
              AND p.InicioUtc > DATEADD(MINUTE, -{duracaoMaximaEmMinutos}, SYSUTCDATETIME())
              AND p.FimUtc > SYSUTCDATETIME()
              AND p.Status NOT IN (3, 4)
            ORDER BY p.InicioUtc DESC
            """).ToListAsync(ct);
        return linhas is [var id, ..] ? id : null;
    }

    public async Task<bool> TetoDePendentesAtingidoAsync(int teto, CancellationToken ct)
    {
        var contagem = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS [Value]
            FROM (SELECT TOP ({teto}) 1 AS X FROM interacao.PedidoMusica WHERE Status = 1) AS t
            """).ToListAsync(ct);
        return contagem.Single() >= teto;
    }

    public async Task<EsperaDoThrottle> EsperaDoFallbackAsync(byte[] deviceHash, byte[] ipHash, Guid? usuarioId, int limitePorIp, CancellationToken ct)
    {
        // Dispositivo e usuário: um pedido nos últimos 5 min bloqueia; falta (5 min − idade do mais recente).
        var dispositivo = await db.Database.SqlQuery<long>($"""
            SELECT TOP (1) DATEDIFF_BIG(MILLISECOND, CriadoEmUtc, SYSUTCDATETIME()) AS [Value]
            FROM interacao.PedidoMusica
            WHERE ListenerDeviceHash = {deviceHash} AND CriadoEmUtc > DATEADD(SECOND, -{JanelaEmSegundos}, SYSUTCDATETIME())
            ORDER BY CriadoEmUtc DESC
            """).ToListAsync(ct);

        IReadOnlyList<long> usuario = [];
        if (usuarioId is { } id)
        {
            usuario = await db.Database.SqlQuery<long>($"""
                SELECT TOP (1) DATEDIFF_BIG(MILLISECOND, CriadoEmUtc, SYSUTCDATETIME()) AS [Value]
                FROM interacao.PedidoMusica
                WHERE UsuarioId = {id} AND CriadoEmUtc > DATEADD(SECOND, -{JanelaEmSegundos}, SYSUTCDATETIME())
                ORDER BY CriadoEmUtc DESC
                """).ToListAsync(ct);
        }

        // IP: os últimos N pedidos da janela; com N deles, a vaga abre quando o mais antigo dos N sair (5 min − idade dele).
        var ip = await db.Database.SqlQuery<long>($"""
            SELECT TOP ({limitePorIp}) DATEDIFF_BIG(MILLISECOND, CriadoEmUtc, SYSUTCDATETIME()) AS [Value]
            FROM interacao.PedidoMusica
            WHERE ListenerIpHash = {ipHash} AND CriadoEmUtc > DATEADD(SECOND, -{JanelaEmSegundos}, SYSUTCDATETIME())
            ORDER BY CriadoEmUtc DESC
            """).ToListAsync(ct);

        return new EsperaDoThrottle(
            dispositivo is [var d, ..] ? Restante(d) : null,
            usuario is [var u, ..] ? Restante(u) : null,
            ip.Count >= limitePorIp ? Restante(ip[^1]) : null);
    }

    // No mínimo 1 s: o Retry-After nunca é 0, nem quando o pedido que bloqueia está na borda da janela.
    private static TimeSpan Restante(long idadeEmMilissegundos)
    {
        var restante = TimeSpan.FromSeconds(JanelaEmSegundos) - TimeSpan.FromMilliseconds(idadeEmMilissegundos);
        return restante < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : restante;
    }
}
