using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using WebRadio.Api.Seguranca;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.DependencyInjection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace WebRadio.Api.Saude;

public sealed class StorageHealthCheck(IObjectStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await storage.VerificarAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("MinIO não responde");
}

public static class SaudeSetup
{
    private const string TagReady = "ready";

    /// <summary>
    /// /health/ready: SQL e MinIO fora → Unhealthy (503); Redis fora → Degraded (200). O Degraded mantém a Api no ar, porque o
    /// anti-spam e o bloqueio de login têm modo degradado (D6, S-A12).
    /// </summary>
    public static IServiceCollection AddSaudeWebRadio(this IServiceCollection services, IConfiguration configuracao)
    {
        var sql = configuracao.GetConnectionString(InfrastructureServiceCollectionExtensions.ConnectionStringName)!;

        services.AddHealthChecks()
            .AddSqlServer(sql, name: "sql", failureStatus: HealthStatus.Unhealthy, tags: [TagReady], timeout: TimeSpan.FromSeconds(5))
            .AddCheck<StorageHealthCheck>("minio", HealthStatus.Unhealthy, [TagReady], TimeSpan.FromSeconds(5))
            .AddRedis(
                sp => sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(),
                name: "redis",
                failureStatus: HealthStatus.Degraded,
                tags: [TagReady],
                timeout: TimeSpan.FromSeconds(5));

        return services;
    }

    /// <summary>S-B05: só no listener :8081. Pela :8080 a rota responde 404 (middleware UseSomenteListenerInterno).</summary>
    public static IEndpointRouteBuilder MapSaude(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = EscreverStatus })
            .SomenteNoListenerInterno().AllowAnonymous().DisableRateLimiting();

        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(TagReady), ResponseWriter = EscreverStatus })
            .SomenteNoListenerInterno().AllowAnonymous().DisableRateLimiting();

        return app;
    }

    private static Task EscreverStatus(HttpContext contexto, HealthReport relatorio)
    {
        contexto.Response.ContentType = "application/json";
        var corpo = new
        {
            status = relatorio.Status.ToString(),
            checks = relatorio.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
        };

        return JsonSerializer.SerializeAsync(contexto.Response.Body, corpo, cancellationToken: contexto.RequestAborted);
    }
}
