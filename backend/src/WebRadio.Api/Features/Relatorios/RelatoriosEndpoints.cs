using MediatR;
using WebRadio.Api.Infra;
using WebRadio.Application.Features.Relatorios;

namespace WebRadio.Api.Features.Relatorios;

/// <summary>02 §11: relatórios do Admin. Só <c>GET</c>, sem corpo.</summary>
public sealed class RelatoriosEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/relatorios").RequireAuthorization("SomenteAdmin");

        g.MapGet("/mais-tocadas", async (DateTime? de, DateTime? ate, int? top, IMediator mediator, CancellationToken ct) =>
            Results.Ok(await mediator.Send(new MaisTocadasQuery(de, ate, top ?? MaisTocadasQuery.TopPadrao), ct)));
    }
}
