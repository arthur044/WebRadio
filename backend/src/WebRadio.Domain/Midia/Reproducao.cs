using WebRadio.Domain.Common;

namespace WebRadio.Domain.Midia;

/// <summary>schema midia, histórico de playout (01-dominio.md §2.7). Só inserção; sem máquina de estados.</summary>
public sealed class Reproducao
{
    public long Id { get; private set; }

    public Guid MidiaId { get; private set; }

    public Guid? PedidoId { get; private set; }

    public Guid? ProgramaId { get; private set; }

    public DateTime IniciadoEmUtc { get; private set; }

    private Reproducao()
    {
    }

    public Reproducao(Guid midiaId, Guid? pedidoId, Guid? programaId, DateTime iniciadoEmUtc)
    {
        UtcGuard.Exigir(iniciadoEmUtc, nameof(iniciadoEmUtc));

        MidiaId = midiaId;
        PedidoId = pedidoId;
        ProgramaId = programaId;
        IniciadoEmUtc = iniciadoEmUtc;
    }
}
