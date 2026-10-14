using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Seguranca;
using WebRadio.Infrastructure.Trabalhos;

namespace WebRadio.Infrastructure.DependencyInjection;

public static class WorkerServiceCollectionExtensions
{
    /// <summary>
    /// E1-F11: banco + relógio + liderança + ciclo de 10 s + manutenção. O Worker não conhece EF (tudo vive aqui) e
    /// NÃO chama <c>AddInfrastructure</c>: ele não recebe JWT nem pepper (04 §Serviços), então o emissor de tokens, o
    /// hasher de IP e o resto da pilha da Api nem chegam a ser registrados no contêiner dele.
    /// <c>Worker:ArquivoDeHeartbeat</c> troca o caminho do heartbeat (padrão /tmp/worker-heartbeat, em tmpfs).
    /// </summary>
    public static IServiceCollection AddWorkerInfrastructure(this IServiceCollection services, IConfiguration configuration,
        bool exigirSenhaDeSegredo = false)
    {
        var connectionString = InfrastructureServiceCollectionExtensions.ConnectionStringDoBanco(configuration, exigirSenhaDeSegredo);
        services.AddRadioDbContext(connectionString);
        services.AddSingleton<IClock, SistemaRelogio>();

        var ciclo = new CicloOptions();
        if (!string.IsNullOrWhiteSpace(configuration["Worker:ArquivoDeHeartbeat"]))
            ciclo.ArquivoDeHeartbeat = configuration["Worker:ArquivoDeHeartbeat"];
        services.AddSingleton(ciclo);
        services.AddSingleton(new LiderancaOptions());

        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<LiderancaDoWorker>(sp, connectionString));
        services.AddSingleton<ILideranca>(sp => sp.GetRequiredService<LiderancaDoWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<LiderancaDoWorker>());
        services.AddScoped<IManutencao, Manutencao>();
        services.AddHostedService<CicloDoWorker>();
        return services;
    }
}
