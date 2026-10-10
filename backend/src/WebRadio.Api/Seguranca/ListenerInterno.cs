using Microsoft.Extensions.Options;
using WebRadio.Api.Configuracao;

namespace WebRadio.Api.Seguranca;

/// <summary>Marca endpoints que só existem no listener interno (:8081): /health/* e /api/v1/internal/*.</summary>
public sealed class SomenteListenerInternoAttribute : Attribute;

public static class ListenerInterno
{
    public static TBuilder SomenteNoListenerInterno<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new SomenteListenerInternoAttribute());

    /// <summary>
    /// Rota inexistente → 404. S-A06: compara com Connection.LocalPort, a porta real do socket. Nunca RequireHost, que compara o cabeçalho Host,
    /// controlado pelo cliente. Precisa rodar depois do UseRouting (o endpoint já está escolhido) e antes de autenticação.
    /// </summary>
    public static IApplicationBuilder UseSomenteListenerInterno(this IApplicationBuilder app) =>
        app.Use(async (contexto, next) =>
        {
            var endpoint = contexto.GetEndpoint();

            // Sem rota correspondente: 404 já aqui. Sem isto, a FallbackPolicy autenticada responderia 401 a qualquer caminho
            // inexistente (inclusive /api/v1/internal/* pela :8080), e o 404 que a spec exige (E1-F12) viraria 401.
            if (endpoint is null)
            {
                contexto.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var interno = endpoint.Metadata.GetMetadata<SomenteListenerInternoAttribute>() is not null;

            if (interno)
            {
                var porta = contexto.RequestServices.GetRequiredService<IOptions<RedeOptions>>().Value.PortaInterna;
                if (contexto.Connection.LocalPort != porta)
                {
                    contexto.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }

            await next();
        });
}
