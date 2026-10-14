using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Trabalhos;

public sealed class CicloOptions
{
    public TimeSpan Intervalo { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan IntervaloDaManutencao { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Arquivo tocado a cada tick, num <c>tmpfs</c> (04 §Serviços): o healthcheck do contêiner confere a idade dele, porque
    /// o Worker é host genérico, sem HTTP nem porta. Nulo = sem heartbeat (testes).
    /// </summary>
    public string? ArquivoDeHeartbeat { get; set; } = "/tmp/worker-heartbeat";
}

/// <summary>
/// E1-F11: laço de 10 s (<c>PeriodicTimer</c>). TODO worker, líder ou não, toca o heartbeat a cada tick; só o LÍDER dispara
/// os jobs — por enquanto a manutenção diária. O job roda fora do tick: um lote longo não atrasa o heartbeat (o
/// healthcheck acusaria worker morto) e nunca há dois jobs ao mesmo tempo. As transições da grade, a expiração de
/// pedidos e a sanitização entram aqui nos próximos épicos.
/// </summary>
public sealed class CicloDoWorker(ILideranca lideranca, IServiceScopeFactory escopos, IClock clock, CicloOptions opcoes,
    ILogger<CicloDoWorker> logger) : BackgroundService
{
    private DateTime? _ultimaManutencaoUtc;
    private bool _heartbeatFalhou;
    private int _ciclos;

    public int Ciclos => Volatile.Read(ref _ciclos);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var job = Task.CompletedTask;
        using var timer = new PeriodicTimer(opcoes.Intervalo);
        try
        {
            do
            {
                Interlocked.Increment(ref _ciclos);
                Heartbeat();
                if (lideranca.EhLider && job.IsCompleted && ManutencaoDevida())
                    job = ManterAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // parada normal do host
        }
        finally
        {
            await job; // ManterAsync nunca lança: só espera o lote em curso perceber o cancelamento
        }
    }

    private bool ManutencaoDevida() =>
        _ultimaManutencaoUtc is not { } ultima || clock.UtcNow - ultima >= opcoes.IntervaloDaManutencao;

    private async Task ManterAsync(CancellationToken ct)
    {
        try
        {
            await using var escopo = escopos.CreateAsyncScope();
            var r = await escopo.ServiceProvider.GetRequiredService<IManutencao>().ExecutarAsync(ct);
            _ultimaManutencaoUtc = clock.UtcNow;
            logger.LogInformation("WORKER_MANUTENCAO: {Pedidos} pedidos anonimizados, {Eventos} eventos e {Refresh} refresh tokens apagados.",
                r.PedidosAnonimizados, r.EventosApagados, r.RefreshApagados);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // parada normal do host
        }
        catch (Exception e)
        {
            // Banco fora, por exemplo: o laço nunca morre (um BackgroundService que lança derruba o host).
            logger.LogError(e, "WORKER_MANUTENCAO: falhou; nova tentativa no próximo ciclo.");
        }
    }

    private void Heartbeat()
    {
        if (opcoes.ArquivoDeHeartbeat is null) return;
        try
        {
            File.WriteAllText(opcoes.ArquivoDeHeartbeat, clock.UtcNow.ToString("O"));
            _heartbeatFalhou = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Avisa uma vez por sequência de falhas; o healthcheck do contêiner é quem acusa o problema.
            if (!_heartbeatFalhou) logger.LogWarning(e, "WORKER_HEARTBEAT: não consegui gravar {Arquivo}.", opcoes.ArquivoDeHeartbeat);
            _heartbeatFalhou = true;
        }
    }
}
