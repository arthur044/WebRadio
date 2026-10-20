using MediatR;
using WebRadio.Api.Infra;
using WebRadio.Application.Features.Stream;

namespace WebRadio.Api.Features.Stream;

/// <summary>02 §8: informação pública do stream. Sem cache: o "no ar" muda a qualquer momento.</summary>
public sealed class StreamEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/stream/info", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-cache";
                return Results.Ok(await mediator.Send(new InfoDoStreamQuery(), ct));
            })
            .AllowAnonymous();
    }
}
