using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebRadio.Infrastructure.Persistencia.Outbox;

/// <summary>
/// E1-F10: laço do outbox dentro da API. Um passe por segundo; lote cheio emenda o próximo. O backoff de um evento que falhou (ex.: Redis do backplane fora) é DELE
/// (<c>EventoOutbox.ProximaTentativaEmUtc</c>, #25): ele só volta à fila depois da espera própria e os demais eventos seguem no ritmo normal. A espera do LAÇO dobra (até 1 min)
/// só quando o passe inteiro falha (banco fora), porque aí nada anda.
/// </summary>
public sealed class OutboxDispatcher(IServiceScopeFactory escopos, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    public static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan EsperaMaxima = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var espera = Intervalo;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var escopo = escopos.CreateAsyncScope();
                var lote = await escopo.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessarLoteAsync(stoppingToken);
                // Falha de entrega de um evento NÃO desacelera o laço: ele já saiu da fila pelo próprio backoff.
                espera = ProximaEspera(espera, falhou: false);
                if (lote.Lidos == OutboxProcessor.TamanhoDoLote) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Banco fora, por exemplo: o laço nunca morre (um BackgroundService que lança derruba o host).
                logger.LogError(ex, "OUTBOX_DISPATCHER: o passe falhou; nova tentativa em {Segundos}s", ProximaEspera(espera, falhou: true).TotalSeconds);
                espera = ProximaEspera(espera, falhou: true);
            }

            try
            {
                await Task.Delay(espera, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public static TimeSpan ProximaEspera(TimeSpan atual, bool falhou) =>
        !falhou ? Intervalo : atual * 2 > EsperaMaxima ? EsperaMaxima : atual * 2;
}
