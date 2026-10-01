using WebRadio.Api.Infra;

namespace WebRadio.Api.Features.Health;

/// <summary>Liveness mínimo. Só no listener interno :8081 (S-B05); os checks de SQL/MinIO/Redis chegam na F08.</summary>
public sealed class HealthEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
        => app.MapGet("/health/live", () => Results.Ok()).AllowAnonymous().RequireInternalListener();
}
