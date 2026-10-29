namespace WebRadio.Application.Abstracoes;

/// <summary>
/// Consultas do pedido de música que dependem do relógio do SQL (<c>SYSUTCDATETIME()</c>, nunca o do contêiner) ou de SQL
/// explícito que o Atlas mede (E3-A01). Implementada na Infrastructure.
/// </summary>
public interface IConsultasDePedidos
{
    /// <summary>
    /// Programa da janela (<c>InicioUtc &lt;= agora &lt; FimUtc</c>, fora de <c>Finalizado</c>/<c>Cancelado</c>), a mesma regra do harbor
    /// (07, E3-D1). Programas do mesmo locutor não se sobrepõem, mas dois locutores podem; vale o de início mais recente.
    /// </summary>
    Task<Guid?> ProgramaDaJanelaAsync(CancellationToken ct);

    /// <summary>
    /// Teto global de <c>Pendente</c> (07, E3-D10): <c>true</c> quando há ao menos <paramref name="teto"/> pedidos pendentes.
    /// O <c>COUNT</c> lê no máximo <paramref name="teto"/> linhas do <c>IX_PedidoMusica_Fila</c>.
    /// </summary>
    Task<bool> TetoDePendentesAtingidoAsync(int teto, CancellationToken ct);

    /// <summary>
    /// Fallback SQL do throttle com o Redis fora (07, E3-F01): quanto falta, nos últimos 5 min, para cada regra liberar uma vaga
    /// (<c>null</c> = regra livre). Roda na transação aberta do <c>INSERT</c> (a trava do dispositivo vale até o commit).
    /// </summary>
    Task<EsperaDoThrottle> EsperaDoFallbackAsync(byte[] deviceHash, byte[] ipHash, Guid? usuarioId, int limitePorIp, CancellationToken ct);
}

/// <summary>Quanto esperar por regra; <c>null</c> = a regra não bloqueia.</summary>
public sealed record EsperaDoThrottle(TimeSpan? Dispositivo, TimeSpan? Usuario, TimeSpan? Ip)
{
    public static readonly EsperaDoThrottle Livre = new(null, null, null);

    /// <summary>O maior tempo entre as regras que bloqueiam, ou <c>null</c> se nenhuma bloqueia.</summary>
    public TimeSpan? Maior()
    {
        TimeSpan? maior = null;
        foreach (var espera in new[] { Dispositivo, Usuario, Ip })
        {
            if (espera is { } valor && (maior is null || valor > maior)) maior = valor;
        }

        return maior;
    }
}
