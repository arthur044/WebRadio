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
                // ATENÇÃO: com EnableRetryOnFailure o EF recusa transações manuais (BeginTransaction) fora de uma
                // execution strategy ("does not support user-initiated transactions"). Todo fluxo que abre
                // transação própria — ex.: sp_getapplock 'grade:programa' (D7), 'seg:admins' — deve rodar assim:
                //   var estrategia = db.Database.CreateExecutionStrategy();
                //   await estrategia.ExecuteAsync(async () => { await using var tx = await db.Database.BeginTransactionAsync(ct); ...; await tx.CommitAsync(ct); });
                // O bloco inteiro é reexecutado no retry: sem efeitos colaterais fora do banco e com o applock
                // pedido dentro da transação (ele morre com ela). SaveChanges isolado já usa a estratégia sozinho.
                sql.EnableRetryOnFailure();
                sql.MigrationsAssembly(typeof(RadioDbContext).Assembly.FullName);
            }));

        services.AddScoped<IRadioDbContext>(sp => sp.GetRequiredService<RadioDbContext>());

        return services;
    }
}
