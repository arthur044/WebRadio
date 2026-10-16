using System.Buffers.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Application.Abstracoes;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using WebRadio.Infrastructure.Armazenamento;
using WebRadio.Infrastructure.Harbor;
using WebRadio.Infrastructure.Triagem;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Eventos;
using WebRadio.Infrastructure.Persistencia.Outbox;
using WebRadio.Infrastructure.Playout;
using WebRadio.Application.Features.Playout;
using WebRadio.Infrastructure.Seed;
using WebRadio.Infrastructure.Seguranca;

namespace WebRadio.Infrastructure.DependencyInjection;

/// <summary>00-arquitetura.md §2.1: Infrastructure implementa e registra via extension methods AddInfrastructure(config).</summary>
public static class InfrastructureServiceCollectionExtensions
{
    public const string ConnectionStringName = "RadioDb";

    /// <summary>
    /// 05 §5.3: a senha NÃO fica na connection string do compose. Ela chega por arquivo (<c>/run/secrets/Banco__Senha</c>,
    /// chave <c>Banco:Senha</c>) e é montada aqui, em memória. Sem <c>Banco:Senha</c>, vale a string como veio (dev local).
    /// </summary>
    public static string ConnectionStringDoBanco(IConfiguration configuration, bool exigirSenhaDeSegredo = false, string? nomeDaAplicacao = null)
    {
        var connectionString = ConnectionStringComSenha(configuration, exigirSenhaDeSegredo);
        // Um Application Name por serviço (docs/conexao-sql-server.md §3): vem do código, não do compose, para a Api e o Worker
        // não herdarem o mesmo nome por uma âncora de YAML.
        return nomeDaAplicacao is null
            ? connectionString
            : new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString) { ApplicationName = nomeDaAplicacao }.ConnectionString;
    }

    private static string ConnectionStringComSenha(IConfiguration configuration, bool exigirSenhaDeSegredo)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Connection string '{ConnectionStringName}' não configurada.");

        // null = sem arquivo de segredo (dev local, senha na própria string). Fora de Development o arquivo é obrigatório.
        var senha = configuration["Banco:Senha"];
        if (senha is null)
        {
            if (exigirSenhaDeSegredo)
                throw new InvalidOperationException("Banco:Senha é obrigatória fora de Development (arquivo de segredo Banco__Senha). Rode infra/scripts/gerar-segredos.sh.");
            return connectionString;
        }

        // S-C02: mesmo fail-fast dos outros segredos, para a Api e para o Worker. Arquivo presente mas vazio, de exemplo ou
        // curto é erro de configuração: sem isto o stack subiria e só o login no SQL falharia, tarde e sem pista. Nunca traz o valor.
        if (ValidacaoDeSegredo.Verificar("Banco:Senha", senha) is { } erro)
            throw new InvalidOperationException(erro);
        return new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString) { Password = senha }.ConnectionString;
    }

    /// <summary>O <see cref="RadioDbContext"/> com a mesma configuração para a Api e para o Worker.</summary>
    internal static IServiceCollection AddRadioDbContext(this IServiceCollection services, string connectionString) =>
        services.AddScoped<DespachanteDeEventosDeDominio>().AddDbContext<RadioDbContext>((escopo, options) =>
            options.ComEventosDeDominio(escopo).UseSqlServer(connectionString, sql =>
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

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddRadioDbContext(ConnectionStringDoBanco(configuration, nomeDaAplicacao: NomesDeAplicacao.Api));

        services.AddScoped<IRadioDbContext>(sp => sp.GetRequiredService<RadioDbContext>());
        services.AddScoped<ITransacaoComTrava, TransacaoComTrava>();
        services.AddScoped<IConsultasDoHarbor, ConsultasDoHarbor>();
        services.AddScoped<ISondaDoBanco, SondaDoBanco>();
        services.AddScoped<IOutbox, OutboxEf>();
        services.AddMemoryCache();
        services.AddScoped<IVersaoDeSeguranca, VersaoDeSeguranca>();

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
        services.AddSingleton<ITocandoAgoraStore>(sp => new TocandoAgoraStore(
            sp.GetRequiredService<IClock>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TocandoAgoraStore>>(), sp.GetService<IConnectionMultiplexer>()));

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

        // Conexão aberta no boot (ConnectAsync) e tolerante a queda: sem Redis a Api sobe e o login usa o fallback em memória.
        services.AddSingleton<ConexaoRedis>();
        services.AddHostedService(sp => sp.GetRequiredService<ConexaoRedis>());
        services.AddSingleton<IConnectionMultiplexer>(sp => sp.GetRequiredService<ConexaoRedis>().Multiplexador);
        services.AddSingleton<RedisLoginThrottle>();
        services.AddSingleton<IRequestThrottle, RedisRequestThrottle>();
        services.AddSingleton<ILoginThrottle>(sp => new LoginThrottleComFallback(
            sp.GetRequiredService<RedisLoginThrottle>(), sp.GetRequiredService<MemoryLoginThrottle>(),
            sp.GetRequiredService<IClock>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LoginThrottleComFallback>>()));
    }
}
