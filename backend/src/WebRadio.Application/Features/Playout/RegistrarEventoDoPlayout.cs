using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Comum;
using WebRadio.Application.Features.Pedidos;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Midia;

namespace WebRadio.Application.Features.Playout;

/// <summary>O que está no ar agora (02 §9, <c>GET /stream/info</c>). <c>PedidoId</c> só existe quando a faixa veio de um pedido.</summary>
public sealed record TocandoAgora(string? Titulo, string? Artista, Guid? MidiaId, Guid? PedidoId, DateTime IniciadoEmUtc);

/// <summary>Última faixa anunciada pelo playout. Em memória e, quando há Redis, compartilhada entre as réplicas.</summary>
public interface ITocandoAgoraStore
{
    Task DefinirAsync(TocandoAgora faixa, CancellationToken ct);

    Task<TocandoAgora?> ObterAsync(CancellationToken ct);
}

/// <summary>02 §8: <c>POST /internal/playout/eventos</c>, enviado pelo Liquidsoap a cada troca de faixa.</summary>
public sealed record RegistrarEventoDoPlayoutCommand(string? Tipo, Guid? MidiaId, Guid? PedidoId, string? Titulo, string? Artista, DateTime? OcorridoEmUtc) : IRequest;

public sealed class RegistrarEventoDoPlayoutValidator : AbstractValidator<RegistrarEventoDoPlayoutCommand>
{
    public const string FaixaIniciada = "FaixaIniciada";
    public const int TamanhoMaximoDoMetadado = MetadadoSeguro.TamanhoMaximo;

    /// <summary>Folga para o relógio do contêiner do playout. Além disso, a data é recusada.</summary>
    public static readonly TimeSpan ToleranciaDeRelogio = TimeSpan.FromMinutes(5);

    public RegistrarEventoDoPlayoutValidator(IClock clock)
    {
        RuleFor(x => x.Tipo).Equal(FaixaIniciada).WithMessage($"Tipo de evento desconhecido (aceito: {FaixaIniciada}).");
        // Sem teto, uma data no futuro deixaria a faixa "vigente" para sempre no tocando agora.
        RuleFor(x => x.OcorridoEmUtc).NotNull().WithMessage("Informe ocorridoEmUtc.")
            .Must(quando => quando is null || quando.Value.ToUniversalTime() <= clock.UtcNow + ToleranciaDeRelogio)
            .WithMessage("ocorridoEmUtc não pode estar no futuro.");
        RuleFor(x => x.MidiaId).NotEqual(Guid.Empty).WithMessage("midiaId inválido.");
        RuleFor(x => x.PedidoId).NotEqual(Guid.Empty).WithMessage("pedidoId inválido.");
        // S-A09: no ao vivo, título e artista vêm do programa do locutor (harbor) e seguem para TODOS os clientes do hub.
        // A regra é a mesma do harbor (MetadadoSeguro), para as duas não se separarem.
        RuleFor(x => x.Titulo)
            .Must(MetadadoSeguro.TamanhoValido).WithMessage($"titulo pode ter no máximo {TamanhoMaximoDoMetadado} caracteres.")
            .Must(MetadadoSeguro.Valido).WithMessage("titulo não pode ter caracteres de controle nem de formato.");
        RuleFor(x => x.Artista)
            .Must(MetadadoSeguro.TamanhoValido).WithMessage($"artista pode ter no máximo {TamanhoMaximoDoMetadado} caracteres.")
            .Must(MetadadoSeguro.Valido).WithMessage("artista não pode ter caracteres de controle nem de formato.");
    }

}

/// <summary>
/// <c>FaixaIniciada</c> (08 E4-F08): atualiza o "tocando agora" e publica <c>TocandoAgora</c> (com o <c>tipoMidia</c> da biblioteca); com <c>midiaId</c> grava a linha de <c>midia.Reproducao</c> (com o
/// <c>PedidoId</c> e o <c>ProgramaId</c> da janela, regra do E3-D1) e, com <c>pedidoId</c> de um pedido <c>Aprovado</c>, marca <c>Tocado</c> e publica <c>PedidoTocado</c>. É idempotente por pedido: o evento
/// repetido (o Liquidsoap reenvia) não duplica a <c>Reproducao</c>, o <c>Tocado</c> nem o evento; o <c>RowVersion</c> decide a corrida com um <c>MarcarTocado</c> manual do locutor.
/// </summary>
public sealed class RegistrarEventoDoPlayoutHandler(ITocandoAgoraStore tocandoAgora, IOutbox outbox, IRadioDbContext db, IConsultasDePedidos consultas, IClock clock)
    : IRequestHandler<RegistrarEventoDoPlayoutCommand>
{
    public async Task Handle(RegistrarEventoDoPlayoutCommand cmd, CancellationToken ct)
    {
        var titulo = Limpar(cmd.Titulo);
        var artista = Limpar(cmd.Artista);
        var iniciadoEmUtc = DateTime.SpecifyKind(cmd.OcorridoEmUtc!.Value.ToUniversalTime(), DateTimeKind.Utc);
        string? tipoMidia = null;
        var repetido = false;

        if (cmd.MidiaId is { } midiaId)
        {
            var midia = await db.ArquivosMidia.AsNoTracking().Where(m => m.Id == midiaId).Select(m => new { m.TipoMidia }).FirstOrDefaultAsync(ct);
            if (midia is not null) // mídia excluída depois de entregue: só o "tocando agora"
            {
                tipoMidia = midia.TipoMidia.ToString();
                Guid? pedidoId = null;
                if (cmd.PedidoId is { } pid && await db.PedidosMusica.FirstOrDefaultAsync(p => p.Id == pid, ct) is { } pedido)
                {
                    pedidoId = pid;
                    repetido = await db.Reproducoes.AnyAsync(r => r.PedidoId == pid, ct);
                    if (!repetido && pedido.Status == StatusPedido.Aprovado)
                    {
                        pedido.MarcarTocado(clock.UtcNow);
                        outbox.EnfileirarParaGrupos(DestinosDoPedido.Destinos(pedido), EventosDoOutbox.PedidoTocado, new { pedidoId = pedido.Id });
                    }
                }

                if (!repetido)
                {
                    var programaId = await consultas.ProgramaDaJanelaAsync(ct);
                    db.Reproducoes.Add(new Reproducao(midiaId, pedidoId, programaId, iniciadoEmUtc));
                }
            }
        }

        if (repetido) return; // o evento já foi tratado: nada de novo para o "tocando agora" nem para o outbox

        await tocandoAgora.DefinirAsync(new TocandoAgora(titulo, artista, cmd.MidiaId, cmd.PedidoId, iniciadoEmUtc), ct);

        // 02 §10: { titulo, artista, tipoMidia, pedidoId? }.
        outbox.EnfileirarParaTodos(EventosDoOutbox.TocandoAgora, new { titulo, artista, tipoMidia, pedidoId = cmd.PedidoId });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Um MarcarTocado manual do locutor ganhou a corrida pelo RowVersion do pedido: o resto deste evento (Reproducao e eventos) não foi salvo; o do outro lado já saiu.
            outbox.DescartarNaoSalvos();
        }
    }

    private static string? Limpar(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
}
