using WebRadio.Domain.Common;

namespace WebRadio.Domain.Interacao;

/// <summary>schema interacao (Announcement). 01-dominio.md §2.6.</summary>
public sealed class Divulgacao : Entidade
{
    public const byte PrioridadeMaxima = 100;

    public string Titulo { get; private set; } = null!;

    public string Mensagem { get; private set; } = null!;

    public string? ImagemChaveStorage { get; private set; }

    public string? LinkDestino { get; private set; }

    public bool Ativo { get; private set; }

    public byte Prioridade { get; private set; }

    public DateTime? InicioExibicaoUtc { get; private set; }

    public DateTime? FimExibicaoUtc { get; private set; }

    public Guid CriadoPorUsuarioId { get; private set; }

    public DateTime CriadoEmUtc { get; private set; }

    public DateTime AtualizadoEmUtc { get; private set; }

    private Divulgacao()
    {
    }

    public Divulgacao(
        Guid id,
        string titulo,
        string mensagem,
        string? imagemChaveStorage,
        string? linkDestino,
        byte prioridade,
        DateTime? inicioExibicaoUtc,
        DateTime? fimExibicaoUtc,
        Guid criadoPorUsuarioId,
        DateTime agora)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titulo);
        ArgumentException.ThrowIfNullOrWhiteSpace(mensagem);
        UtcGuard.Exigir(agora, nameof(agora));
        ValidarPrioridade(prioridade);
        var link = NormalizarLink(linkDestino);
        ValidarJanela(inicioExibicaoUtc, fimExibicaoUtc);

        Titulo = titulo;
        Mensagem = mensagem;
        ImagemChaveStorage = imagemChaveStorage;
        LinkDestino = link;
        Prioridade = prioridade;
        InicioExibicaoUtc = inicioExibicaoUtc;
        FimExibicaoUtc = fimExibicaoUtc;
        CriadoPorUsuarioId = criadoPorUsuarioId;
        Ativo = true;
        CriadoEmUtc = agora;
        AtualizadoEmUtc = agora;
    }

    public bool EstaAtivaParaPublico(DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));

        return Ativo
            && (InicioExibicaoUtc is null || InicioExibicaoUtc <= agora)
            && (FimExibicaoUtc is null || agora < FimExibicaoUtc);
    }

    public void Editar(
        string titulo,
        string mensagem,
        string? imagemChaveStorage,
        string? linkDestino,
        byte prioridade,
        DateTime? inicioExibicaoUtc,
        DateTime? fimExibicaoUtc,
        DateTime agora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titulo);
        ArgumentException.ThrowIfNullOrWhiteSpace(mensagem);
        UtcGuard.Exigir(agora, nameof(agora));
        ValidarPrioridade(prioridade);
        var link = NormalizarLink(linkDestino);
        ValidarJanela(inicioExibicaoUtc, fimExibicaoUtc);

        Titulo = titulo;
        Mensagem = mensagem;
        ImagemChaveStorage = imagemChaveStorage;
        LinkDestino = link;
        Prioridade = prioridade;
        InicioExibicaoUtc = inicioExibicaoUtc;
        FimExibicaoUtc = fimExibicaoUtc;
        AtualizadoEmUtc = agora;
    }

    public void Ativar(DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));
        Ativo = true;
        AtualizadoEmUtc = agora;
    }

    public void Desativar(DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));
        Ativo = false;
        AtualizadoEmUtc = agora;
    }

    private static void ValidarPrioridade(byte prioridade)
    {
        if (prioridade > PrioridadeMaxima)
        {
            throw new ArgumentOutOfRangeException(nameof(prioridade), $"Prioridade precisa estar entre 0 e {PrioridadeMaxima}.");
        }
    }

    /// <summary>S-B03 (<see cref="LinkDeDivulgacao"/>): grava a forma canônica. <c>null</c> = sem link.</summary>
    private static string? NormalizarLink(string? link) =>
        link is null
            ? null
            : LinkDeDivulgacao.Normalizar(link)
              ?? throw new ArgumentException("LinkDestino precisa ser uma URL https:// com host DNS público (sem IP, localhost, .local, .internal, usuário ou espaço).", nameof(link));

    private static void ValidarJanela(DateTime? inicioUtc, DateTime? fimUtc)
    {
        if (inicioUtc is not null)
        {
            UtcGuard.Exigir(inicioUtc.Value, nameof(inicioUtc));
        }

        if (fimUtc is not null)
        {
            UtcGuard.Exigir(fimUtc.Value, nameof(fimUtc));
        }

        if (inicioUtc is not null && fimUtc is not null && fimUtc <= inicioUtc)
        {
            throw new ArgumentException("FimExibicaoUtc precisa ser maior que InicioExibicaoUtc.", nameof(fimUtc));
        }
    }
}
