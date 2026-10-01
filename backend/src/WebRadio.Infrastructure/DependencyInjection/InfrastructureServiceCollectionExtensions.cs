using System.Buffers.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Seguranca;

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

        // Segurança. Chaves lidas sob demanda (1º uso), depois do ValidateOnStart da Api, que já recusou segredo
        // vazio/curto/__GERAR__. Decodificação base64url, nunca Convert.FromBase64String (05 §5.1).
        services.AddSingleton<IClock, SistemaRelogio>();
        // Teto global de PBKDF2 simultâneos; vazio = uma vaga por CPU visível (cgroup). Ajustável: Seguranca__HashMaxConcorrencia.
        services.AddSingleton<ISenhaHasher>(sp =>
            int.TryParse(sp.GetRequiredService<IConfiguration>()["Seguranca:HashMaxConcorrencia"], out var max) && max >= 1
                ? new SenhaHasher(max, TimeSpan.FromSeconds(5))
                : new SenhaHasher());
        services.AddSingleton<ISenhasVazadas, SenhasVazadas>();
        services.AddSingleton<ILoginThrottle, MemoryLoginThrottle>();
        services.AddSingleton<IIpHasher>(sp =>
            new IpHasher(Base64Url.DecodeFromChars(sp.GetRequiredService<IConfiguration>()["Seguranca:Pepper"]!)));
        services.AddSingleton<IEmissorDeTokens>(sp =>
            new EmissorDeTokens(Base64Url.DecodeFromChars(sp.GetRequiredService<IConfiguration>()["Jwt:SigningKey"]!),
                sp.GetRequiredService<IClock>()));

        return services;
    }
}
