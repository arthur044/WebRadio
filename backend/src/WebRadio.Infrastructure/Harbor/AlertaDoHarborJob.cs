using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Features.Harbor;

namespace WebRadio.Infrastructure.Harbor;

/// <summary>
/// E2-F12: esvazia a <see cref="FilaDeAlertasDoHarbor"/> fora do caminho do <c>harbor/auth</c>. Cada alerta roda num escopo próprio
/// (consulta do dono e outbox numa transação só dele). Uma falha de um alerta nunca derruba o laço nem o host. Dois laços independentes:
/// o de credenciais espera <see cref="EspacoEntreConsultas"/> depois de cada consulta do dono (no máximo 30 por minuto; o excesso fica
/// na fila e, cheia, é descartado), e o do global não espera por ninguém.
/// </summary>
public sealed class AlertaDoHarborJob(FilaDeAlertasDoHarbor fila, IServiceScopeFactory escopos, ILogger<AlertaDoHarborJob> logger) : BackgroundService
{
    /// <summary>Só os testes mudam isto.</summary>
    public TimeSpan EspacoEntreConsultas { get; init; } = FilaDeAlertasDoHarbor.EspacoEntreConsultas;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Laco(fila.LerCredencialAsync, EspacoEntreConsultas, stoppingToken),
        Laco(fila.LerGlobalAsync, TimeSpan.Zero, stoppingToken));

    private async Task Laco(Func<CancellationToken, ValueTask<AlertaPendente>> ler, TimeSpan espacoDepoisDeCadaAlerta, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pendente = await ler(stoppingToken);
                await using var escopo = escopos.CreateAsyncScope();
                await escopo.ServiceProvider.GetRequiredService<AlertaDoHarbor>().ProcessarAsync(pendente, stoppingToken);
                if (espacoDepoisDeCadaAlerta > TimeSpan.Zero) await Task.Delay(espacoDepoisDeCadaAlerta, stoppingToken);
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
