using FluentValidation;
using MediatR;
using WebRadio.Application.Abstracoes;

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
    public const int TamanhoMaximoDoMetadado = 200;

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
        RuleFor(x => x.Titulo)
            .MaximumLength(TamanhoMaximoDoMetadado).WithMessage($"titulo pode ter no máximo {TamanhoMaximoDoMetadado} caracteres.")
            .Must(SemCaractereDeControle).WithMessage("titulo não pode ter caracteres de controle.");
        RuleFor(x => x.Artista)
            .MaximumLength(TamanhoMaximoDoMetadado).WithMessage($"artista pode ter no máximo {TamanhoMaximoDoMetadado} caracteres.")
            .Must(SemCaractereDeControle).WithMessage("artista não pode ter caracteres de controle.");
    }

    /// <summary>
    /// Controle C0/C1 (inclusive quebra de linha, tab e NUL) e os separadores de linha/parágrafo do Unicode, que também
    /// quebram linha: servem para forjar linha de log ou de protocolo. Também as marcas e os controles de direção
    /// (LRM/RLM/ALM, embeddings, overrides e isolates): invertem o texto exibido a todos os ouvintes (spoofing visual).
    /// Acento, emoji (inclusive com ZWJ) e outros alfabetos passam.
    /// </summary>
    private static bool SemCaractereDeControle(string? texto) =>
        texto is null || !texto.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029' || c is '\u061C' or '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069'));
}

/// <summary>
/// Esqueleto do Épico 1: só atualiza o "tocando agora" e publica <c>TocandoAgora</c>. O registro em <c>Reproducao</c> e a
/// marcação do pedido como tocado entram com o playout real (Épico 4).
/// </summary>
public sealed class RegistrarEventoDoPlayoutHandler(ITocandoAgoraStore tocandoAgora, IOutbox outbox, IRadioDbContext db)
    : IRequestHandler<RegistrarEventoDoPlayoutCommand>
{
    public async Task Handle(RegistrarEventoDoPlayoutCommand cmd, CancellationToken ct)
    {
        var titulo = Limpar(cmd.Titulo);
        var artista = Limpar(cmd.Artista);
        var iniciadoEmUtc = DateTime.SpecifyKind(cmd.OcorridoEmUtc!.Value.ToUniversalTime(), DateTimeKind.Utc);

        await tocandoAgora.DefinirAsync(new TocandoAgora(titulo, artista, cmd.MidiaId, cmd.PedidoId, iniciadoEmUtc), ct);

        // 02 §10: { titulo, artista, tipoMidia, pedidoId? }. O tipo da mídia só é conhecido quando o playout real
        // consultar ArquivoMidia (Épico 4); até lá vai nulo.
        outbox.EnfileirarParaTodos(EventosDoOutbox.TocandoAgora, new { titulo, artista, tipoMidia = (string?)null, pedidoId = cmd.PedidoId });
        await db.SaveChangesAsync(ct);
    }

    private static string? Limpar(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
}
