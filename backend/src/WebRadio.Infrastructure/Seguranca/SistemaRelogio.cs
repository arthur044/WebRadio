using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Seguranca;

public sealed class SistemaRelogio : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
