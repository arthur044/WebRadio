using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Comum;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Interacao;

namespace WebRadio.Application.Features.Pedidos;

/// <summary>
/// Carga <c>{ pedidoId, status, motivoRejeicao? }</c> do <c>PedidoModerado</c> (02 §10). Os destinos saem de <see cref="Destinos"/>:
/// <c>locutores</c> sempre e <c>listener:{hash}</c> quando o hash existe (pedido anonimizado só vai para <c>locutores</c>, E3-F03).
/// </summary>
public sealed record PedidoModeradoEvento(Guid PedidoId, string Status, string? MotivoRejeicao);

internal static class DestinosDoPedido
{
    public static IReadOnlyCollection<string> Destinos(PedidoMusica pedido) =>
        pedido.ListenerDeviceHash is { } hash ? [GruposDoHub.Locutores, GruposDoHub.Listener(hash)] : [GruposDoHub.Locutores];

    /// <summary>O <c>RowVersion</c> divergente (outro locutor moderou entre a leitura e o UPDATE) é o mesmo 409 de um pedido já moderado.</summary>
    public static async Task SalvarAsync(IRadioDbContext db, Guid pedidoId, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new PedidoJaModeradoException(pedidoId);
        }
    }

    public static async Task<PedidoMusica> CarregarAsync(IRadioDbContext db, Guid id, CancellationToken ct) =>
        await db.PedidosMusica.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NaoEncontradoException("Pedido");
}

/// <summary>
/// <c>midiaId</c>, quando vem, precisa ser uma <c>ArquivoMidia</c> <c>Aprovado</c>, <c>Ativo</c> e do tipo <c>Musica</c>, senão 400. No
/// Épico 3 nenhuma mídia está <c>Aprovado</c> (a biblioteca é do Épico 4), então qualquer <c>midiaId</c> responde 400.
/// </summary>
public sealed record AprovarPedidoCommand(Guid Id, Guid ModeradorId, Guid? MidiaId) : IRequest<PedidoDto>;

public sealed class AprovarPedidoHandler(IRadioDbContext db, IOutbox outbox, IClock clock) : IRequestHandler<AprovarPedidoCommand, PedidoDto>
{
    public async Task<PedidoDto> Handle(AprovarPedidoCommand cmd, CancellationToken ct)
    {
        var pedido = await DestinosDoPedido.CarregarAsync(db, cmd.Id, ct);

        if (cmd.MidiaId is { } midiaId)
        {
            var valida = await db.ArquivosMidia.AsNoTracking().AnyAsync(m => m.Id == midiaId && m.StatusSanitizacao == StatusSanitizacao.Aprovado
                && m.Ativo && m.TipoMidia == TipoMidia.Musica, ct);
            if (!valida)
                throw new ValidationException([new ValidationFailure("MidiaId", "A mídia precisa ser uma música aprovada e ativa.")]);
        }

        pedido.Aprovar(cmd.ModeradorId, cmd.MidiaId, clock.UtcNow);
        outbox.EnfileirarParaGrupos(DestinosDoPedido.Destinos(pedido), EventosDoOutbox.PedidoModerado,
            new PedidoModeradoEvento(pedido.Id, nameof(StatusPedido.Aprovado), null));
        await DestinosDoPedido.SalvarAsync(db, pedido.Id, ct);
        return PedidoDto.De(pedido);
    }
}

/// <summary><c>motivo</c> de 1 a 280 unidades UTF-16 pelo <see cref="TextoSeguro"/> (E3-D7): o ouvinte o lê em "Meus pedidos".</summary>
public sealed record RejeitarPedidoCommand(Guid Id, Guid ModeradorId, string Motivo) : IRequest<PedidoDto>;

public sealed class RejeitarPedidoValidator : AbstractValidator<RejeitarPedidoCommand>
{
    public const int TamanhoMotivo = 280;

    public RejeitarPedidoValidator() => RuleFor(x => x.Motivo).TextoSeguro(TamanhoMotivo);
}

public sealed class RejeitarPedidoHandler(IRadioDbContext db, IOutbox outbox, IClock clock) : IRequestHandler<RejeitarPedidoCommand, PedidoDto>
{
    public async Task<PedidoDto> Handle(RejeitarPedidoCommand cmd, CancellationToken ct)
    {
        var pedido = await DestinosDoPedido.CarregarAsync(db, cmd.Id, ct);
        var motivo = TextoSeguro.Normalizar(cmd.Motivo);

        pedido.Rejeitar(cmd.ModeradorId, motivo, clock.UtcNow);
        outbox.EnfileirarParaGrupos(DestinosDoPedido.Destinos(pedido), EventosDoOutbox.PedidoModerado,
            new PedidoModeradoEvento(pedido.Id, nameof(StatusPedido.Rejeitado), motivo));
        await DestinosDoPedido.SalvarAsync(db, pedido.Id, ct);
        return PedidoDto.De(pedido);
    }
}

/// <summary>Só <c>Aprovado</c>: qualquer outro status é o 409 <c>/erros/pedido-ja-moderado</c> (02 §5), não a transição inválida do domínio.</summary>
public sealed record MarcarPedidoTocadoCommand(Guid Id) : IRequest<PedidoDto>;

public sealed class MarcarPedidoTocadoHandler(IRadioDbContext db, IOutbox outbox, IClock clock) : IRequestHandler<MarcarPedidoTocadoCommand, PedidoDto>
{
    public async Task<PedidoDto> Handle(MarcarPedidoTocadoCommand cmd, CancellationToken ct)
    {
        var pedido = await DestinosDoPedido.CarregarAsync(db, cmd.Id, ct);
        if (pedido.Status != StatusPedido.Aprovado) throw new PedidoJaModeradoException(pedido.Id);

        pedido.MarcarTocado(clock.UtcNow);
        outbox.EnfileirarParaGrupos(DestinosDoPedido.Destinos(pedido), EventosDoOutbox.PedidoTocado, new { pedidoId = pedido.Id });
        await DestinosDoPedido.SalvarAsync(db, pedido.Id, ct);
        return PedidoDto.De(pedido);
    }
}
