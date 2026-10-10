namespace WebRadio.Infrastructure.Persistencia.Outbox;

/// <summary>
/// infra.EventoOutbox (01-dominio.md §2.8). Mecanismo técnico, não conceito de domínio: vive em Infrastructure
/// e é gravado na mesma transação da mudança de estado (D22). O OutboxDispatcher (E1-F10) é quem consome.
/// </summary>
public sealed class EventoOutbox
{
    public long Id { get; private set; }

    public string Tipo { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public DateTime CriadoEmUtc { get; private set; }

    public DateTime? ProcessadoEmUtc { get; private set; }

    public short Tentativas { get; private set; }

    public string? UltimoErro { get; private set; }

    private EventoOutbox()
    {
    }

    public EventoOutbox(string tipo, string payload)
    {
        Tipo = tipo;
        Payload = payload;
        CriadoEmUtc = DateTime.UtcNow;
    }
}
