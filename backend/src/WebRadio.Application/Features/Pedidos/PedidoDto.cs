using WebRadio.Domain.Interacao;

namespace WebRadio.Application.Features.Pedidos;

/// <summary>
/// 02 §5: <c>{ id, nomeOuvinte, tituloMusica, artista, mensagem, status, criadoEmUtc, moderadoEmUtc, motivoRejeicao, tocadoEmUtc,
/// programaId }</c>. Nenhum hash (de dispositivo ou de IP) sai daqui, nem o <c>UsuarioId</c>.
/// </summary>
public sealed record PedidoDto(
    Guid Id,
    string NomeOuvinte,
    string TituloMusica,
    string Artista,
    string? Mensagem,
    string Status,
    DateTime CriadoEmUtc,
    DateTime? ModeradoEmUtc,
    string? MotivoRejeicao,
    DateTime? TocadoEmUtc,
    Guid? ProgramaId)
{
    public static PedidoDto De(PedidoMusica p) =>
        new(p.Id, p.NomeOuvinte, p.TituloMusica, p.Artista, p.Mensagem, p.Status.ToString(), p.CriadoEmUtc, p.ModeradoEmUtc,
            p.MotivoRejeicao, p.TocadoEmUtc, p.ProgramaId);
}
