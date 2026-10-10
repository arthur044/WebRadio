namespace WebRadio.Application.Abstracoes;

/// <summary>Seeders de boot (AdminSeeder, DevDataSeeder). Idempotentes: rodar duas vezes não duplica nada.</summary>
public interface IInicializadorDeDados
{
    Task ExecutarAsync(CancellationToken cancellationToken = default);
}
