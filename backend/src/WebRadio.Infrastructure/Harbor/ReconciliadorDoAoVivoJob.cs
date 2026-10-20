using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Features.Harbor;

namespace WebRadio.Infrastructure.Harbor;

/// <summary>
/// Dentro da API (é onde o estado do ao vivo e o outbox vivem): a cada 10 s vê se o "no ar" venceu (fim + 1 min) e, se venceu,
/// publica <c>TransmissaoAoVivoAlterada</c>. Com várias réplicas, a troca condicional do store deixa só uma publicar.
/// </summary>
public sealed class ReconciliadorDoAoVivoJob(IServiceScopeFactory escopos, ILogger<ReconciliadorDoAoVivoJob> logger) : BackgroundService
{
    public static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var escopo = escopos.CreateAsyncScope();
                await escopo.ServiceProvider.GetRequiredService<ReconciliadorDoAoVivo>().ExecutarAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Banco fora, por exemplo: o laço nunca morre (um BackgroundService que lança derruba o host).
                logger.LogError(ex, "HARBOR_RECONCILIADOR: a passada falhou; nova tentativa em {Segundos}s", Intervalo.TotalSeconds);
            }

            try
            {
                await Task.Delay(Intervalo, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
