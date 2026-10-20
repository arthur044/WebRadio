using MediatR;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Harbor;
using WebRadio.Application.Features.Playout;

namespace WebRadio.Application.Features.Stream;

public sealed record TocandoAgoraDto(string? Titulo, string? Artista, string? Tipo);

public sealed record AoVivoDto(bool NoAr, Guid? ProgramaId, string? LocutorNome);

/// <summary>02 §8: <c>GET /stream/info</c>. <c>aoVivo</c> a partir do Épico 2; o cliente ressincroniza por aqui ao reconectar ao hub.</summary>
public sealed record InfoDoStreamDto(string Url, string Formato, int BitrateKbps, TocandoAgoraDto? TocandoAgora, AoVivoDto AoVivo);

public sealed record InfoDoStreamQuery : IRequest<InfoDoStreamDto>;

public sealed class InfoDoStreamHandler(ITocandoAgoraStore tocandoAgora, IEstadoDoAoVivoStore aoVivo) : IRequestHandler<InfoDoStreamQuery, InfoDoStreamDto>
{
    public const string Url = "/stream/radio.mp3";
    public const string Formato = "audio/mpeg";
    public const int BitrateKbps = 128;

    public async Task<InfoDoStreamDto> Handle(InfoDoStreamQuery q, CancellationToken ct)
    {
        // O tipo da mídia só é conhecido com o playout real (Épico 4): até lá vai nulo, como no evento TocandoAgora.
        var faixa = await tocandoAgora.ObterAsync(ct) is { } f ? new TocandoAgoraDto(f.Titulo, f.Artista, null) : null;
        var estado = await aoVivo.ObterAsync(ct);
        var ao = estado is { NoAr: true } ? new AoVivoDto(true, estado.ProgramaId, estado.LocutorNome) : new AoVivoDto(false, null, null);
        return new InfoDoStreamDto(Url, Formato, BitrateKbps, faixa, ao);
    }
}
