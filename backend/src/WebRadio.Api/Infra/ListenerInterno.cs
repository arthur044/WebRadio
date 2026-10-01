namespace WebRadio.Api.Infra;

/// <summary>
/// S-A06: endpoints internos (/internal, /health) só valem no listener Kestrel <c>:8081</c>. A checagem usa a
/// porta LOCAL do socket, que o cliente não controla. NUNCA <c>RequireHost</c>: ele compara o cabeçalho Host.
/// </summary>
public static class ListenerInterno
{
    public const int PortaInterna = 8081;

    public static TBuilder RequireInternalListener<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.Connection.LocalPort == PortaInterna ? await next(ctx) : Results.NotFound());
        return builder;
    }
}
