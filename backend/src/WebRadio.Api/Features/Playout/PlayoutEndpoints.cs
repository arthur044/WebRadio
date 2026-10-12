using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using WebRadio.Api.Infra;
using WebRadio.Api.Segredos;
using WebRadio.Application.Features.Playout;

namespace WebRadio.Api.Features.Playout;

public sealed record EventoDoPlayoutRequest(string? Tipo, Guid? MidiaId, Guid? PedidoId, string? Titulo, string? Artista, DateTime? OcorridoEmUtc);

/// <summary>
/// 02 §8: rotas que só o Liquidsoap chama, em <c>http://api:8081/api/v1/internal/...</c>. Duas barreiras independentes:
/// o listener (porta local do socket; pela :8080 a rota responde 404, antes de qualquer autenticação) e o Bearer
/// <c>Playout__Token</c>. Não usam JWT de usuário, por isso o grupo é AllowAnonymous e o token é conferido no filtro.
/// </summary>
public sealed class PlayoutEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var interno = app.MapGroup("/api/v1/internal")
            .AllowAnonymous()
            .RequireInternalListener()
            .AddEndpointFilter<TokenDoPlayout>();

        // Esqueleto do Épico 1: sem fila de pedidos ainda, a rotação local do Liquidsoap assume (204 = sem faixa).
        interno.MapGet("/playout/proxima", () => Results.NoContent());

        interno.MapPost("/playout/eventos", async (EventoDoPlayoutRequest req, IMediator mediator, CancellationToken ct) =>
        {
            await mediator.Send(new RegistrarEventoDoPlayoutCommand(req.Tipo, req.MidiaId, req.PedidoId, req.Titulo, req.Artista, req.OcorridoEmUtc), ct);
            return Results.Accepted();
        });
    }
}

/// <summary>
/// Bearer do playout (S-A06). Aceita o token atual e, durante a rotação, o anterior. A comparação é sobre o SHA-256 dos
/// dois lados com <c>FixedTimeEquals</c>: tempo constante e sem revelar o tamanho do segredo.
/// </summary>
public sealed class TokenDoPlayout(IOptions<PlayoutOptions> opcoes) : IEndpointFilter
{
    private const string Esquema = "Bearer ";

    private readonly byte[][] _aceitos = new[] { opcoes.Value.Token, opcoes.Value.TokenAnterior }
        .Where(t => !string.IsNullOrWhiteSpace(t))
        .Select(t => SHA256.HashData(Encoding.UTF8.GetBytes(t!)))
        .ToArray();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var cabecalho = context.HttpContext.Request.Headers.Authorization.ToString();
        if (!cabecalho.StartsWith(Esquema, StringComparison.OrdinalIgnoreCase) || !Confere(cabecalho[Esquema.Length..].Trim()))
        {
            context.HttpContext.Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
            return Results.Unauthorized();
        }

        return await next(context);
    }

    private bool Confere(string apresentado)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apresentado));
        var confere = false;
        // Sem atalho: compara com todos os aceitos, para o tempo não dizer qual deles (ou nenhum) bateu.
        foreach (var aceito in _aceitos) confere |= CryptographicOperations.FixedTimeEquals(hash, aceito);
        return confere;
    }
}
