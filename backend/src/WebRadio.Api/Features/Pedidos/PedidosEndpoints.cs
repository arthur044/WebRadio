using System.Security.Claims;
using MediatR;
using WebRadio.Api.Infra;
using WebRadio.Application.Features.Pedidos;
using WebRadio.Infrastructure.Seguranca;

namespace WebRadio.Api.Features.Pedidos;

public sealed record CriarPedidoRequest(string? NomeOuvinte, string? TituloMusica, string? Artista, string? Mensagem);

public sealed record AprovarPedidoRequest(Guid? MidiaId);

public sealed record RejeitarPedidoRequest(string? Motivo);

/// <summary>
/// 02 §5. Criar e "meus pedidos" são públicos, identificados pelo <c>X-Listener-Id</c> (UUID) ou pelo Bearer; fila e moderação são do
/// <c>PodeModerar</c>. O IP do anti-spam é o do socket, já corrigido pelo ForwardedHeaders (só o nginx da rede <c>app</c> é confiável,
/// S-A11): o corpo e o <c>X-Forwarded-For</c> do cliente nunca chegam a ele.
/// </summary>
public sealed class PedidosEndpoints : IEndpointModule
{
    public const string HeaderDoOuvinte = "X-Listener-Id";

    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/pedidos");

        g.MapPost("", async (CriarPedidoRequest req, HttpContext http, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
            {
                var pedido = await mediator.Send(new CriarPedidoCommand(
                    LerOuvinte(http), http.Connection.RemoteIpAddress, UsuarioDoBearer(user),
                    req.NomeOuvinte ?? "", req.TituloMusica ?? "", req.Artista ?? "", req.Mensagem), ct);
                return Results.Json(pedido, statusCode: StatusCodes.Status201Created);
            })
            .AllowAnonymous();

        g.MapGet("/meus", async (HttpContext http, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new MeusPedidosQuery(LerOuvinte(http), UsuarioDoBearer(user)), ct)))
            .AllowAnonymous();

        g.MapGet("", async (string? status, int? pagina, int? tamanho, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new ListarPedidosQuery(status, pagina ?? 1, tamanho ?? 20), ct)))
            .RequireAuthorization("PodeModerar");

        g.MapPost("/{id:guid}/aprovar", async (Guid id, AprovarPedidoRequest? req, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
                Moderador(user) is { } moderador
                    ? Results.Ok(await mediator.Send(new AprovarPedidoCommand(id, moderador, req?.MidiaId), ct))
                    : Results.Unauthorized())
            .RequireAuthorization("PodeModerar");

        g.MapPost("/{id:guid}/rejeitar", async (Guid id, RejeitarPedidoRequest req, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
                Moderador(user) is { } moderador
                    ? Results.Ok(await mediator.Send(new RejeitarPedidoCommand(id, moderador, req.Motivo ?? ""), ct))
                    : Results.Unauthorized())
            .RequireAuthorization("PodeModerar");

        g.MapPost("/{id:guid}/marcar-tocado", async (Guid id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new MarcarPedidoTocadoCommand(id), ct)))
            .RequireAuthorization("PodeModerar");
    }

    /// <summary><c>X-Listener-Id</c> em UUID (formato D) e diferente de zero; qualquer outra coisa vira <c>Guid.Empty</c>, e a validação responde 400.</summary>
    private static Guid LerOuvinte(HttpContext http) =>
        http.Request.Headers.TryGetValue(HeaderDoOuvinte, out var valores) && valores.Count == 1
        && Guid.TryParseExact(valores[0], "D", out var id) ? id : Guid.Empty;

    /// <summary>Id do usuário quando há Bearer válido. O token restrito de troca de senha não vale para nada além da troca.</summary>
    private static Guid? UsuarioDoBearer(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && !user.HasClaim(EmissorDeTokens.ClaimDeveTrocarSenha, "true")
        && Guid.TryParse(user.FindFirstValue("sub"), out var id) ? id : null;

    private static Guid? Moderador(ClaimsPrincipal user) => Guid.TryParse(user.FindFirstValue("sub"), out var id) ? id : null;
}
