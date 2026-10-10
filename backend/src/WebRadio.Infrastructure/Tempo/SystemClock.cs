using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Tempo;

public sealed class SystemClock : IClock
{
    public DateTime UtcAgora => DateTime.UtcNow;
}
