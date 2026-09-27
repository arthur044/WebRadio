using WebRadio.Domain.Common;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;

namespace WebRadio.Domain.Interacao;

/// <summary>schema interacao (SongRequest). 01-dominio.md §2.4.</summary>
public sealed class PedidoMusica : Entidade
{
    public const int TamanhoHashBytes = 32;

    public Guid? ProgramaId { get; private set; }

    public Guid? UsuarioId { get; private set; }

    public string NomeOuvinte { get; private set; } = null!;

    public string TituloMusica { get; private set; } = null!;

    public string Artista { get; private set; } = null!;

    public string? Mensagem { get; private set; }

    public byte[]? ListenerDeviceHash { get; private set; }

    public byte[]? ListenerIpHash { get; private set; }

    public StatusPedido Status { get; private set; }

    public Guid? MidiaId { get; private set; }

    public Guid? ModeradoPorUsuarioId { get; private set; }

    public DateTime? ModeradoEmUtc { get; private set; }

    public string? MotivoRejeicao { get; private set; }

    public DateTime? EnviadoPlayoutEmUtc { get; private set; }

    public DateTime? TocadoEmUtc { get; private set; }

    public DateTime CriadoEmUtc { get; private set; }

    private PedidoMusica()
    {
    }

    public PedidoMusica(
        Guid id,
        Guid? programaId,
        Guid? usuarioId,
        string nomeOuvinte,
        string tituloMusica,
        string artista,
        string? mensagem,
        byte[]? listenerDeviceHash,
        byte[]? listenerIpHash,
        DateTime agora)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nomeOuvinte);
        ArgumentException.ThrowIfNullOrWhiteSpace(tituloMusica);
        ArgumentException.ThrowIfNullOrWhiteSpace(artista);
        ValidarHash(listenerDeviceHash, nameof(listenerDeviceHash));
        ValidarHash(listenerIpHash, nameof(listenerIpHash));
        UtcGuard.Exigir(agora, nameof(agora));

        ProgramaId = programaId;
        UsuarioId = usuarioId;
        NomeOuvinte = nomeOuvinte;
        TituloMusica = tituloMusica;
        Artista = artista;
        Mensagem = mensagem;
        ListenerDeviceHash = listenerDeviceHash;
        ListenerIpHash = listenerIpHash;
        Status = StatusPedido.Pendente;
        CriadoEmUtc = agora;
    }

    /// <summary>midiaId precisa ser de uma mídia Aprovado do tipo Musica — checado no handler (Application).</summary>
    public void Aprovar(Guid moderadorId, Guid? midiaId, DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));
        GarantirPendente();

        Status = StatusPedido.Aprovado;
        MidiaId = midiaId;
        ModeradoPorUsuarioId = moderadorId;
        ModeradoEmUtc = agora;
    }

    public void Rejeitar(Guid moderadorId, string motivo, DateTime agora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        UtcGuard.Exigir(agora, nameof(agora));
        GarantirPendente();

        Status = StatusPedido.Rejeitado;
        ModeradoPorUsuarioId = moderadorId;
        ModeradoEmUtc = agora;
        MotivoRejeicao = motivo;
    }

    public void MarcarEnviadoAoPlayout(DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));
        GarantirAprovado("enviar ao playout");
        EnviadoPlayoutEmUtc = agora;
    }

    /// <summary>Disparado tanto pelo evento FaixaIniciada do playout quanto por MarcarTocado manual do locutor.</summary>
    public void MarcarTocado(DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));
        GarantirAprovado("marcar como Tocado");

        Status = StatusPedido.Tocado;
        TocadoEmUtc = agora;
    }

    public void Expirar(TimeSpan tempoParaExpiracao, DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));
        GarantirPendente();

        if (CriadoEmUtc > agora - tempoParaExpiracao)
        {
            throw new TransicaoInvalidaException("O pedido ainda não atingiu o prazo de expiração.");
        }

        Status = StatusPedido.Expirado;
    }

    private void GarantirPendente()
    {
        if (Status != StatusPedido.Pendente)
        {
            throw new PedidoJaModeradoException(Id);
        }
    }

    private void GarantirAprovado(string acao)
    {
        if (Status != StatusPedido.Aprovado)
        {
            throw new TransicaoInvalidaException($"Só é possível {acao} um pedido Aprovado (status atual: {Status}).");
        }
    }

    private static void ValidarHash(byte[]? hash, string nomeParametro)
    {
        if (hash is not null && hash.Length != TamanhoHashBytes)
        {
            throw new ArgumentException($"O hash precisa ter {TamanhoHashBytes} bytes (HMAC-SHA256).", nomeParametro);
        }
    }
}
