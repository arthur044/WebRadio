using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Application.Autenticacao;
using WebRadio.Application.Comum;

namespace WebRadio.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(AssemblyMarker).Assembly;

        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(assembly);
            // Ordem: o primeiro registrado é o mais externo (Logging → Validation → handler).
            cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });

        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);
        services.AddScoped<LoginBloqueio>();

        return services;
    }
}
