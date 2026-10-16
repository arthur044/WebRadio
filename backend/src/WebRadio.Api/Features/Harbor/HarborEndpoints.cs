using MediatR;
using WebRadio.Api.Features.Playout;
using WebRadio.Api.Infra;
using WebRadio.Application.Features.Harbor;

namespace WebRadio.Api.Features.Harbor;

public sealed record HarborAuthRequest(string? Usuario, string? Senha, string? EnderecoIp)
{
    public override string ToString() => nameof(HarborAuthRequest);
}

/// <summary>
/// 06 "Endpoints internos": o <c>auth</c> e a consulta periódica do <c>input.harbor</c> do Liquidsoap. Mesmas barreiras das rotas do
/// playout (listener :8081 e Bearer <c>Playout__Token</c>). 401 e 403 saem SEM corpo: nada diz qual condição falhou.
/// </summary>
public sealed class HarborEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var harbor = app.MapGroup("/api/v1/internal/harbor")
            .AllowAnonymous()
            .RequireInternalListener()
            .AddEndpointFilter<TokenDoPlayout>();

        harbor.MapPost("/auth", async (HarborAuthRequest req, HttpContext http, IMediator mediator, CancellationToken ct) =>
            await mediator.Send(new AutenticarNoHarborCommand(req.Usuario, req.Senha, req.EnderecoIp), ct) is { } janela
                ? Results.Ok(new { programaId = janela.ProgramaId, fimUtc = janela.FimUtc })
                : SemCorpo(http, StatusCodes.Status401Unauthorized));

        harbor.MapGet("/autorizacao/{identificador}", async (string identificador, HttpContext http, IMediator mediator, CancellationToken ct) =>
            await mediator.Send(new AutorizacaoDoHarborQuery(identificador), ct) is { } janela
                ? Results.Ok(new { programaId = janela.ProgramaId, fimUtc = janela.FimUtc })
                : SemCorpo(http, StatusCodes.Status403Forbidden));
    }

    /// <summary>Sem o ProblemDetails que o UseStatusCodePages acrescentaria: a recusa não descreve nada.</summary>
    private static IResult SemCorpo(HttpContext http, int status)
    {
        if (http.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } paginas) paginas.Enabled = false;
        return Results.StatusCode(status);
    }
}
