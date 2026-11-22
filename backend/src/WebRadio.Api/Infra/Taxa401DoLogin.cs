namespace WebRadio.Api.Infra;

/// <summary>
/// #9 (R1 do Sentinel): contagem de respostas 401 de <c>POST /api/v1/auth/login</c> (senha errada, conta inexistente), lida e zerada uma vez por minuto pelo
/// <see cref="Taxa401DoLoginJob"/>. Só o login: o 401 de outras rotas (token expirado) não é sinal de força bruta. Sem PII: só um número.
/// </summary>
public sealed class ContadorDe401DoLogin
{
    private long _total;

    public void Registrar() => Interlocked.Increment(ref _total);

    /// <summary>Devolve o que contou desde a última coleta e zera.</summary>
    public long Coletar() => Interlocked.Exchange(ref _total, 0);
}

/// <summary>
/// Log estruturado <c>AUTH_401_TAXA</c> (Warning) a cada minuto em que houve 401 no login: <c>Total</c>, <c>JanelaSegundos</c> e <c>PorSegundo</c>. Ainda não há sistema de alerta;
/// o operador (ou um filtro de log) procura o marcador. Minuto sem 401 não loga (sem ruído). Uma falha ao logar nunca derruba o host.
/// </summary>
public sealed class Taxa401DoLoginJob(ContadorDe401DoLogin contador, ILogger<Taxa401DoLoginJob> logger) : BackgroundService
{
    public const int JanelaSegundos = 60;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var relogio = new PeriodicTimer(TimeSpan.FromSeconds(JanelaSegundos));
        try
        {
            while (await relogio.WaitForNextTickAsync(stoppingToken)) Emitir();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Uma janela: lê e zera o contador e loga se houve 401. Público para o teste não esperar um minuto.</summary>
    public void Emitir()
    {
        var total = contador.Coletar();
        if (total == 0) return;
        logger.LogWarning("AUTH_401_TAXA total={Total} janelaSegundos={JanelaSegundos} porSegundo={PorSegundo}", total, JanelaSegundos, total / (double)JanelaSegundos);
    }
}

public static class Taxa401DoLoginExtensions
{
    private const string CaminhoDoLogin = "/api/v1/auth/login";

    public static IServiceCollection AddTaxa401DoLogin(this IServiceCollection services)
    {
        services.AddSingleton<ContadorDe401DoLogin>();
        services.AddHostedService<Taxa401DoLoginJob>();
        return services;
    }

    /// <summary>Por FORA do exception handler: o status que conta é o final da resposta (a exceção de credenciais vira 401 lá dentro).</summary>
    public static IApplicationBuilder UseContagemDe401DoLogin(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            await next();
            if (ctx.Response.StatusCode == StatusCodes.Status401Unauthorized
                && HttpMethods.IsPost(ctx.Request.Method)
                && ctx.Request.Path.Equals(CaminhoDoLogin, StringComparison.OrdinalIgnoreCase))
                ctx.RequestServices.GetRequiredService<ContadorDe401DoLogin>().Registrar();
        });
}
