using System.Buffers.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Application.Abstracoes;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using WebRadio.Infrastructure.Armazenamento;
using WebRadio.Infrastructure.Triagem;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Seed;
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
        services.AddSingleton<MemoryLoginThrottle>();
        AddThrottleDeLogin(services, configuration);
        services.AddSingleton<IIpHasher>(sp =>
            new IpHasher(Base64Url.DecodeFromChars(sp.GetRequiredService<IConfiguration>()["Seguranca:Pepper"]!)));
        services.AddSingleton<IEmissorDeTokens>(sp =>
            new EmissorDeTokens(Base64Url.DecodeFromChars(sp.GetRequiredService<IConfiguration>()["Jwt:SigningKey"]!),
                sp.GetRequiredService<IClock>()));

        services.AddScoped<IRefreshTokenService>(sp => new RefreshTokenService(
            sp.GetRequiredService<RadioDbContext>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<IEmissorDeTokens>(),
            Base64Url.DecodeFromChars(sp.GetRequiredService<IConfiguration>()["Seguranca:Pepper"]!),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RefreshTokenService>>()));

        services.AddSingleton<IListenerIdentity>(sp => new ListenerIdentity(
            Base64Url.DecodeFromChars(sp.GetRequiredService<IConfiguration>()["Seguranca:Pepper"]!), sp.GetRequiredService<IIpHasher>()));
        AddStorage(services, configuration);

        services.AddScoped<IInicializadorDeDados, AdminSeeder>();

        return services;
    }

    /// <summary>
    /// S-A02: o scanner NoOp exige as DUAS condições (Development E Seguranca:PermitirScannerNoOp=true). Fora delas não há
    /// IMalwareScanner registrado: quem depende dele falha ao resolver (fail-closed), em vez de aceitar arquivo sem scan.
    /// </summary>
    public static IServiceCollection AddScannerNoOpSePermitido(this IServiceCollection services, IConfiguration configuration, IHostEnvironment ambiente)
    {
        if (ambiente.IsDevelopment() && bool.TryParse(configuration["Seguranca:PermitirScannerNoOp"], out var permitido) && permitido)
            services.AddSingleton<IMalwareScanner, NoOpMalwareScanner>();
        return services;
    }

    /// <summary>
    /// Storage é opcional em Development (sem Storage:Endpoint não há IObjectStorage); fora dele a Api exige a seção
    /// inteira e valida a credencial no boot (S-C02).
    /// </summary>
    private static void AddStorage(IServiceCollection services, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["Storage:Endpoint"])) return;

        services.AddSingleton(_ => new StorageOptions(
            configuration["Storage:Endpoint"]!,
            configuration["Storage:PublicEndpoint"]!,
            configuration["Storage:AccessKey"]!,
            configuration["Storage:SecretKey"]!,
            bool.TryParse(configuration["Storage:UsarSsl"], out var ssl) && ssl,
            string.IsNullOrWhiteSpace(configuration["Storage:Regiao"]) ? "us-east-1" : configuration["Storage:Regiao"]!));
        services.AddSingleton<IObjectStorage, MinioObjectStorage>();
    }

    /// <summary>Seeder de desenvolvimento: a Api só chama isto em Development (o repositório é público).</summary>
    public static IServiceCollection AddDevDataSeeder(this IServiceCollection services) =>
        services.AddScoped<IInicializadorDeDados, DevDataSeeder>();

    /// <summary>
    /// Com Redis:Host configurado, o bloqueio de login vai para o Redis (compartilhado entre réplicas) e cai para a memória se
    /// ele falhar. Sem Redis:Host (só aceito em Development, a Api exige fora dele) fica só a memória.
    /// O IRequestThrottle segue a mesma escolha, mas SEM fallback: falha do Redis propaga e quem chama decide (D6).
    /// </summary>
    private static void AddThrottleDeLogin(IServiceCollection services, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["Redis:Host"]))
        {
            services.AddSingleton<ILoginThrottle>(sp => sp.GetRequiredService<MemoryLoginThrottle>());
            services.AddSingleton<IRequestThrottle, MemoryRequestThrottle>();
            return;
        }

        // Lazy no primeiro uso e tolerante a queda: sem Redis a Api sobe e o login usa o fallback em memória.
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var opcoes = new ConfigurationOptions
            {
                Password = configuration["Redis:Password"],
                AbortOnConnectFail = false,
                BacklogPolicy = BacklogPolicy.FailFast,
                ConnectTimeout = 1000,
                AsyncTimeout = 500,
                SyncTimeout = 500,
                ClientName = "webradio-api",
            };
            opcoes.EndPoints.Add(configuration["Redis:Host"]!, int.TryParse(configuration["Redis:Porta"], out var porta) ? porta : 6379);
            return ConnectionMultiplexer.Connect(opcoes);
        });
        services.AddSingleton<RedisLoginThrottle>();
        services.AddSingleton<IRequestThrottle, RedisRequestThrottle>();
        services.AddSingleton<ILoginThrottle>(sp => new LoginThrottleComFallback(
            sp.GetRequiredService<RedisLoginThrottle>(), sp.GetRequiredService<MemoryLoginThrottle>(),
            sp.GetRequiredService<IClock>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LoginThrottleComFallback>>()));
    }
}
