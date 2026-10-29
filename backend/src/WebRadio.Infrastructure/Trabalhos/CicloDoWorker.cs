using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Trabalhos;

public sealed class CicloOptions
{
    public TimeSpan Intervalo { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan IntervaloDaManutencao { get; set; } = TimeSpan.FromHours(24);

    /// <summary>07 E3-F04: a expiração dos pedidos pendentes roda a cada 60 s (o tick do laço é de 10 s).</summary>
    public TimeSpan IntervaloDaExpiracao { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Manutenção que falha (banco fora, por exemplo) não é refeita a cada ciclo de 10 s: espera este tempo, que dobra a
    /// cada falha seguida até <see cref="RecuoMaximoDaManutencao"/> (B3-07).
    /// </summary>
    public TimeSpan RecuoInicialDaManutencao { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan RecuoMaximoDaManutencao { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Arquivo tocado a cada tick, num <c>tmpfs</c> (04 §Serviços): o healthcheck do contêiner confere a idade dele, porque
    /// o Worker é host genérico, sem HTTP nem porta. Nulo = sem heartbeat (testes).
    /// </summary>
    public string? ArquivoDeHeartbeat { get; set; } = "/tmp/worker-heartbeat";
}

/// <summary>
/// E1-F11: laço de 10 s (<c>PeriodicTimer</c>). TODO worker, líder ou não, toca o heartbeat a cada tick; só o LÍDER dispara
/// os jobs — por enquanto a manutenção diária. O job roda fora do tick: um lote longo não atrasa o heartbeat (o
/// healthcheck acusaria worker morto) e nunca há dois jobs ao mesmo tempo. As transições da grade (E2-F03) rodam a CADA tick
/// do líder, em paralelo à manutenção (cada uma com a própria vaga); a expiração de pedidos e a sanitização entram aqui nos
/// próximos épicos.
/// </summary>
public sealed class CicloDoWorker(ILideranca lideranca, IServiceScopeFactory escopos, IClock clock, CicloOptions opcoes,
    ILogger<CicloDoWorker> logger) : BackgroundService
{
    private int _falhasDasTransicoes;
    private int _falhasDaExpiracao;
    private DateTime? _ultimaExpiracaoUtc;
    private DateTime? _ultimaManutencaoUtc;
    private DateTime? _proximaTentativaUtc;
    private int _falhasDaManutencao;
    private bool _heartbeatFalhou;
    private int _ciclos;

    public int Ciclos => Volatile.Read(ref _ciclos);

    /// <summary>Quantas vezes seguidas a manutenção falhou (zera ao dar certo). Os testes esperam por ele antes de mexer no relógio.</summary>
    public int FalhasSeguidasDaManutencao => Volatile.Read(ref _falhasDaManutencao);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var job = Task.CompletedTask;
        var transicoes = Task.CompletedTask;
        var expiracao = Task.CompletedTask;
        using var timer = new PeriodicTimer(opcoes.Intervalo);
        try
        {
            do
            {
                Interlocked.Increment(ref _ciclos);
                Heartbeat();
                if (lideranca.EhLider && job.IsCompleted && ManutencaoDevida())
                    job = ManterAsync(stoppingToken);
                if (lideranca.EhLider && transicoes.IsCompleted)
                    transicoes = TransicionarAsync(stoppingToken);
                if (lideranca.EhLider && expiracao.IsCompleted && ExpiracaoDevida())
                    expiracao = ExpirarPedidosAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // parada normal do host
        }
        finally
        {
            await job; // ManterAsync nunca lança: só espera o lote em curso perceber o cancelamento
            await transicoes; // idem
            await expiracao; // idem
        }
    }

    /// <summary>
    /// E2-F03. Cada tick do líder: dois UPDATEs baratos, sem recuo (o próximo ciclo é em 10 s). Falha não derruba o laço; o erro (com
    /// a stack) sai UMA vez por sequência de falhas, as seguintes saem como aviso curto, e o retorno é registrado.
    /// </summary>
    private async Task TransicionarAsync(CancellationToken ct)
    {
        try
        {
            await using var escopo = escopos.CreateAsyncScope();
            var r = await escopo.ServiceProvider.GetRequiredService<ITransicoesDaGrade>().ExecutarAsync(ct);
            var falhas = Interlocked.Exchange(ref _falhasDasTransicoes, 0);
            if (falhas > 0) logger.LogInformation("WORKER_TRANSICOES: voltou a funcionar depois de {Falhas} falha(s) seguida(s).", falhas);
            if (r.Iniciados + r.Finalizados > 0)
                logger.LogInformation("WORKER_TRANSICOES: {Iniciados} programa(s) ao vivo e {Finalizados} finalizado(s).", r.Iniciados, r.Finalizados);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // parada normal do host
        }
        catch (Exception e)
        {
            if (_falhasDasTransicoes == 0)
                logger.LogError(e, "WORKER_TRANSICOES: falhou; tenta de novo no próximo ciclo e só repete o erro depois que voltar a funcionar.");
            else
                logger.LogWarning("WORKER_TRANSICOES: segue falhando ({Tipo}).", e.GetType().Name);
            Interlocked.Increment(ref _falhasDasTransicoes);
        }
    }

    private bool ExpiracaoDevida() =>
        _ultimaExpiracaoUtc is not { } ultima || clock.UtcNow - ultima >= opcoes.IntervaloDaExpiracao;

    /// <summary>
    /// E3-F04. A cada 60 s do líder: <c>Pendente → Expirado</c> em lotes, com o <c>PedidoModerado</c> no outbox. Falha não derruba o laço; o
    /// erro (com a stack) sai UMA vez por sequência de falhas, as seguintes saem como aviso curto. Tenta de novo no próximo intervalo.
    /// </summary>
    private async Task ExpirarPedidosAsync(CancellationToken ct)
    {
        _ultimaExpiracaoUtc = clock.UtcNow;
        try
        {
            await using var escopo = escopos.CreateAsyncScope();
            var expirados = await escopo.ServiceProvider.GetRequiredService<IExpiracaoDePedidos>().ExecutarAsync(ct);
            var falhas = Interlocked.Exchange(ref _falhasDaExpiracao, 0);
            if (falhas > 0) logger.LogInformation("WORKER_EXPIRACAO_PEDIDOS: voltou a funcionar depois de {Falhas} falha(s) seguida(s).", falhas);
            if (expirados > 0) logger.LogInformation("WORKER_EXPIRACAO_PEDIDOS: {Pedidos} pedido(s) pendente(s) expirado(s).", expirados);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // parada normal do host
        }
        catch (Exception e)
        {
            if (_falhasDaExpiracao == 0)
                logger.LogError(e, "WORKER_EXPIRACAO_PEDIDOS: falhou; tenta de novo no próximo intervalo e só repete o erro depois que voltar a funcionar.");
            else
                logger.LogWarning("WORKER_EXPIRACAO_PEDIDOS: segue falhando ({Tipo}).", e.GetType().Name);
            Interlocked.Increment(ref _falhasDaExpiracao);
        }
    }

    private bool ManutencaoDevida()
    {
        if (_proximaTentativaUtc is { } recuo && clock.UtcNow < recuo) return false; // ainda no recuo de uma falha
        return _ultimaManutencaoUtc is not { } ultima || clock.UtcNow - ultima >= opcoes.IntervaloDaManutencao;
    }

    private async Task ManterAsync(CancellationToken ct)
    {
        try
        {
            await using var escopo = escopos.CreateAsyncScope();
            var r = await escopo.ServiceProvider.GetRequiredService<IManutencao>().ExecutarAsync(ct);
            _ultimaManutencaoUtc = clock.UtcNow;
            _proximaTentativaUtc = null;
            var falhas = Interlocked.Exchange(ref _falhasDaManutencao, 0);
            if (falhas > 0) logger.LogInformation("WORKER_MANUTENCAO: voltou a funcionar depois de {Falhas} falha(s) seguida(s).", falhas);
            logger.LogInformation("WORKER_MANUTENCAO: {Pedidos} pedidos anonimizados, {Eventos} eventos, {Refresh} refresh tokens e {Credenciais} credenciais de transmissão apagados.",
                r.PedidosAnonimizados, r.EventosApagados, r.RefreshApagados, r.CredenciaisApagadas);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // parada normal do host
        }
        catch (Exception e)
        {
            // Banco fora, por exemplo: o laço nunca morre (um BackgroundService que lança derruba o host). Recuo exponencial,
            // e um único erro (com a stack) por sequência de falhas: as seguintes saem como aviso curto, sem a stack.
            var recuo = RecuoDepoisDe(_falhasDaManutencao);
            _proximaTentativaUtc = clock.UtcNow + recuo;
            if (_falhasDaManutencao == 0)
                logger.LogError(e, "WORKER_MANUTENCAO: falhou; nova tentativa em {Recuo}, com recuo crescente, e sem repetir este erro enquanto a falha durar.", recuo);
            else
                logger.LogWarning("WORKER_MANUTENCAO: segue falhando ({Tipo}); nova tentativa em {Recuo}.", e.GetType().Name, recuo);
            Interlocked.Increment(ref _falhasDaManutencao);
        }
    }

    private TimeSpan RecuoDepoisDe(int falhasAnteriores)
    {
        // Dobra a cada falha seguida; o limite do expoente evita estourar o TimeSpan.
        var recuo = opcoes.RecuoInicialDaManutencao * Math.Pow(2, Math.Min(falhasAnteriores, 20));
        return recuo > opcoes.RecuoMaximoDaManutencao ? opcoes.RecuoMaximoDaManutencao : recuo;
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
