using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;
using WebRadio.Api.Infra;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Api.Features.Health;

/// <summary>
/// E1-F08. <c>/health/live</c> (o processo responde) e <c>/health/ready</c> (dependências), só no listener interno
/// :8081 (S-B05); pela :8080 as duas rotas respondem 404.
/// </summary>
public sealed class HealthEndpoints : IEndpointModule
{
    public const string TagReady = "ready";
    public const string Sql = "sql";
    public const string Minio = "minio";
    public const string Redis = "redis";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public void Map(IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = Escrever })
            .AllowAnonymous().RequireInternalListener();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(TagReady), ResponseWriter = Escrever })
            .AllowAnonymous().RequireInternalListener();
    }

    /// <summary>
    /// SQL e MinIO fora → Unhealthy (503). Redis fora → Degraded (200): a Api continua servindo, porque o bloqueio de
    /// login e o anti-spam têm modo degradado (D6, S-A12). MinIO e Redis só entram se estiverem configurados.
    /// </summary>
    public static IServiceCollection AddHealthChecksDaApi(IServiceCollection services, IConfiguration configuration)
    {
        var checks = services.AddHealthChecks()
            .AddCheck<SqlHealthCheck>(Sql, HealthStatus.Unhealthy, [TagReady], Timeout);
        if (!string.IsNullOrWhiteSpace(configuration["Storage:Endpoint"]))
            checks.AddCheck<StorageHealthCheck>(Minio, HealthStatus.Unhealthy, [TagReady], Timeout);
        if (!string.IsNullOrWhiteSpace(configuration["Redis:Host"]))
            checks.AddCheck<RedisHealthCheck>(Redis, HealthStatus.Degraded, [TagReady], Timeout);
        return services;
    }

    /// <summary>Só o estado de cada dependência: descrição e exceção ficam no log, nunca na resposta.</summary>
    private static Task Escrever(HttpContext contexto, HealthReport relatorio)
    {
        contexto.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(contexto.Response.Body, new
        {
            status = relatorio.Status.ToString(),
            checks = relatorio.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
        }, cancellationToken: contexto.RequestAborted);
    }
}

public sealed class SqlHealthCheck(ISondaDoBanco banco) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => await banco.ConectaAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus, "SQL Server não responde");
}

public sealed class StorageHealthCheck(IObjectStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => await storage.VerificarAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus, "MinIO não responde");
}

public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception e) when (e is RedisException or TimeoutException or ObjectDisposedException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis não responde", e);
        }
    }
}
