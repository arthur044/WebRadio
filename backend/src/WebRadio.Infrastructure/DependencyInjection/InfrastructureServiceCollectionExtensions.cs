using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Opcoes;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Redis;
using WebRadio.Infrastructure.Scanner;
using WebRadio.Infrastructure.Seed;
using WebRadio.Infrastructure.Seguranca;
using WebRadio.Infrastructure.Storage;
using WebRadio.Infrastructure.Tempo;

namespace WebRadio.Infrastructure.DependencyInjection;

/// <summary>00-arquitetura.md §2.1: Infrastructure implementa e registra via extension methods AddInfrastructure(config).</summary>
public static class InfrastructureServiceCollectionExtensions
{
    public const string ConnectionStringName = "RadioDb";

    /// <summary>Base compartilhada por Api, Worker e Migrator: EF Core/SQL Server e o relógio.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' não configurada.");

        if (ValidadorDeSegredos.EhPlaceholder(connectionString) || connectionString.Contains("__GERAR__", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Connection string '{ConnectionStringName}' vazia ou com valor de exemplo (__GERAR__).");
        }

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
        services.TryAddSingleton<IClock, SystemClock>();

        return services;
    }

    /// <summary>
    /// Serviços exclusivos da Api (E1-F07, E1-F09): segurança, Redis, storage e seeders. Toda opção com segredo é validada
    /// no boot (S-C02): segredo vazio ou __GERAR__ impede a aplicação de subir.
    /// </summary>
    public static IServiceCollection AddInfrastructureApi(this IServiceCollection services, IConfiguration configuration, IHostEnvironment ambiente)
    {
        services.AddInfrastructure(configuration);

        AddOpcao<JwtOptions, JwtOptionsValidator>(services, configuration, JwtOptions.Secao);
        AddOpcao<SegurancaOptions, SegurancaOptionsValidator>(services, configuration, SegurancaOptions.Secao);
        AddOpcao<RedisOptions, RedisOptionsValidator>(services, configuration, RedisOptions.Secao);
        AddOpcao<StorageOptions, StorageOptionsValidator>(services, configuration, StorageOptions.Secao);
        AddOpcao<SeedOptions, SeedOptionsValidator>(services, configuration, SeedOptions.Secao);

        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IJwtService, JwtService>();
        services.AddSingleton<IListenerIdentity, ListenerIdentityService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();

        // Redis é lazy e tolerante a queda (AbortOnConnectFail = false): sem ele a Api sobe e o health fica Degraded.
        services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(MontarRedis(sp.GetRequiredService<IOptions<RedisOptions>>().Value)));
        services.AddSingleton<IRequestThrottle, RedisRequestThrottle>();

        services.AddSingleton<IObjectStorage, MinioObjectStorage>();

        // S-A02: o scanner NoOp precisa das duas condições. Fora delas não há IMalwareScanner registrado (fail-closed).
        if (ambiente.IsDevelopment() && configuration.GetValue<bool>($"{SegurancaOptions.Secao}:{nameof(SegurancaOptions.PermitirScannerNoOp)}"))
        {
            services.AddSingleton<IMalwareScanner, NoOpMalwareScanner>();
        }

        services.AddScoped<IInicializadorDeDados, AdminSeeder>();
        services.AddScoped<IInicializadorDeDados, DevDataSeeder>();

        return services;
    }

    private static void AddOpcao<TOpcoes, TValidador>(IServiceCollection services, IConfiguration configuration, string secao)
        where TOpcoes : class
        where TValidador : class, IValidateOptions<TOpcoes>
    {
        services.AddOptions<TOpcoes>().Bind(configuration.GetSection(secao)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<TOpcoes>, TValidador>();
    }

    private static ConfigurationOptions MontarRedis(RedisOptions o)
    {
        var config = new ConfigurationOptions
        {
            Password = o.Password,
            AbortOnConnectFail = false,
            // Redis fora nao pode custar segundos por comando no caminho do login: falha rapido em vez de enfileirar (backlog).
            BacklogPolicy = BacklogPolicy.FailFast,
            ConnectTimeout = 1000,
            AsyncTimeout = 500,
            SyncTimeout = 500,
            ClientName = "webradio-api",
        };

        config.EndPoints.Add(o.Host, o.Porta);
        return config;
    }
}
