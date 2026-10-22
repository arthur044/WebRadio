using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Features.Harbor;

namespace WebRadio.Infrastructure.Harbor;

/// <summary>
/// E2-F12: esvazia a <see cref="FilaDeAlertasDoHarbor"/> fora do caminho do <c>harbor/auth</c>. Cada alerta roda num escopo próprio
/// (consulta do dono e outbox numa transação só dele). Uma falha de um alerta nunca derruba o laço nem o host. Depois de cada consulta
/// do dono espera <see cref="FilaDeAlertasDoHarbor.EspacoEntreConsultas"/> (no máximo 30 por minuto): o excesso fica na fila e, cheia, é descartado.
/// </summary>
public sealed class AlertaDoHarborJob(FilaDeAlertasDoHarbor fila, IServiceScopeFactory escopos, ILogger<AlertaDoHarborJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pendente = await fila.LerAsync(stoppingToken);
                await using var escopo = escopos.CreateAsyncScope();
                await escopo.ServiceProvider.GetRequiredService<AlertaDoHarbor>().ProcessarAsync(pendente, stoppingToken);
                if (AlertaDoHarbor.ConsultaODono(pendente)) await Task.Delay(FilaDeAlertasDoHarbor.EspacoEntreConsultas, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // O AlertaDoHarbor já captura as falhas do próprio alerta; isto cobre o resto (criar o escopo, por exemplo).
                logger.LogWarning(ex, "HARBOR_ALERTA_FALHOU: o serviço de alertas do harbor falhou ao tratar um alerta");
            }
        }
    }
}
