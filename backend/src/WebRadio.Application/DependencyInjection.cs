using FluentValidation;
using MediatR;
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
        return services;
    }
}
