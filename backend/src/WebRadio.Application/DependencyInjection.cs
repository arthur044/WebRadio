using FluentValidation;
using MediatR;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Harbor;
using Microsoft.Extensions.DependencyInjection;
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
        });
        services.AddValidatorsFromAssembly(typeof(AssemblyMarker).Assembly);
        services.AddScoped<ThrottleDoHarbor>();

        // D22: reações aos eventos de domínio, despachadas pelo interceptor da Infrastructure antes do commit.
        foreach (var tipo in typeof(AssemblyMarker).Assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (var contrato in tipo.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>)))
                services.AddScoped(contrato, tipo);
        }

        return services;
    }
}
