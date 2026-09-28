using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.DependencyInjection;

/// <summary>00-arquitetura.md §2.1: Infrastructure implementa e registra via extension methods AddInfrastructure(config).</summary>
public static class InfrastructureServiceCollectionExtensions
{
    public const string ConnectionStringName = "RadioDb";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' não configurada.");

        services.AddDbContext<RadioDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure();
                sql.MigrationsAssembly(typeof(RadioDbContext).Assembly.FullName);
            }));

        services.AddScoped<IRadioDbContext>(sp => sp.GetRequiredService<RadioDbContext>());

        return services;
    }
}
