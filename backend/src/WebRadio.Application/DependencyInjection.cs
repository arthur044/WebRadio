using FluentValidation;
using MediatR;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Harbor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Pipeline;

namespace WebRadio.Application;

/// <summary>00-arquitetura.md §2.1: Application registra MediatR, validadores e o pipeline Logging → Validation.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(AssemblyMarker).Assembly);
            // A ordem de registro é a ordem de execução: Logging envolve Validation.
            cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            // Logging → Validation → Throttle → Handler (Fluxo B): só os requests IThrottledRequest passam pelo Throttle.
            cfg.AddOpenBehavior(typeof(ThrottleBehavior<,>));
        });
        services.AddValidatorsFromAssembly(typeof(AssemblyMarker).Assembly);
        services.AddScoped<ThrottleDoHarbor>();
        services.AddScoped<AlertaDoHarbor>();
        services.AddSingleton<WebRadio.Application.Features.Pedidos.DisjuntorDoRedis>();
        services.AddSingleton<WebRadio.Application.Features.Pedidos.CacheDoTeto>();
        services.AddScoped<WebRadio.Application.Features.Pedidos.EstadoDoThrottle>();
        services.AddScoped<WebRadio.Application.Pipeline.IThrottleDoRequest<WebRadio.Application.Features.Pedidos.CriarPedidoCommand>, WebRadio.Application.Features.Pedidos.ThrottleDePedido>();
        services.AddSingleton(sp => new FilaDeAlertasDoHarbor(sp.GetRequiredService<ILogger<FilaDeAlertasDoHarbor>>()));

        // D22: reações aos eventos de domínio, despachadas pelo interceptor da Infrastructure antes do commit.
        foreach (var tipo in typeof(AssemblyMarker).Assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (var contrato in tipo.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>)))
                services.AddScoped(contrato, tipo);
        }

        return services;
    }
}
