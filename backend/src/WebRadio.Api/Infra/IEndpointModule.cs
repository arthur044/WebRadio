namespace WebRadio.Api.Infra;

/// <summary>Uma feature registra as próprias Minimal APIs; o Program descobre os módulos por reflexão e pelo DI.</summary>
public interface IEndpointModule
{
    void Map(IEndpointRouteBuilder app);
}

public static class EndpointModuleExtensions
{
    public static IServiceCollection AddEndpointModules(this IServiceCollection services)
    {
        foreach (var tipo in typeof(IEndpointModule).Assembly.GetTypes()
                     .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IEndpointModule).IsAssignableFrom(t)))
            services.AddSingleton(typeof(IEndpointModule), tipo);
        return services;
    }

    public static void MapEndpointModules(this IEndpointRouteBuilder app)
    {
        foreach (var modulo in app.ServiceProvider.GetServices<IEndpointModule>())
            modulo.Map(app);
    }
}
