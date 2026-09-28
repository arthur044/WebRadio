using WebRadio.Domain.Common;
using WebRadio.Domain.Erros;

namespace WebRadio.Domain.Autenticacao;

/// <summary>schema seg. Usar um token já revogado revoga toda a FamiliaId (sinal de roubo) — 01-dominio.md §2.2.</summary>
public sealed class RefreshToken : Entidade
{
    public const int TamanhoHashBytes = 32;

    public Guid UsuarioId { get; private set; }

    public byte[] TokenHash { get; private set; } = [];

    public Guid FamiliaId { get; private set; }

    public DateTime ExpiraEmUtc { get; private set; }

    public DateTime CriadoEmUtc { get; private set; }

    public byte[]? CriadoPorIpHash { get; private set; }

    public DateTime? RevogadoEmUtc { get; private set; }

    public Guid? SubstituidoPorId { get; private set; }

    public bool EstaRevogado => RevogadoEmUtc is not null;

    private RefreshToken()
    {
    }

    public RefreshToken(
        Guid id,
        Guid usuarioId,
        byte[] tokenHash,
        Guid familiaId,
        DateTime criadoEmUtc,
        TimeSpan duracao,
        byte[]? criadoPorIpHash)
        : base(id)
    {
        if (tokenHash.Length != TamanhoHashBytes)
        {
            throw new ArgumentException($"TokenHash precisa ter {TamanhoHashBytes} bytes (SHA-256).", nameof(tokenHash));
        }

        if (criadoPorIpHash is not null && criadoPorIpHash.Length != TamanhoHashBytes)
        {
            throw new ArgumentException($"CriadoPorIpHash precisa ter {TamanhoHashBytes} bytes (HMAC-SHA256).", nameof(criadoPorIpHash));
        }

        if (duracao <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duracao), "A duração precisa ser positiva.");
        }

        UtcGuard.Exigir(criadoEmUtc, nameof(criadoEmUtc));

        UsuarioId = usuarioId;
        TokenHash = (byte[])tokenHash.Clone();
        FamiliaId = familiaId;
        CriadoEmUtc = criadoEmUtc;
        ExpiraEmUtc = criadoEmUtc.Add(duracao);
        CriadoPorIpHash = (byte[]?)criadoPorIpHash?.Clone();
    }

    public bool EstaValido(DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));
        return !EstaRevogado && ExpiraEmUtc > agora;
    }

    public void Revogar(DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));

        if (EstaRevogado)
        {
            return; // idempotente: revogar duas vezes não é erro.
        }

        RevogadoEmUtc = agora;
    }

    /// <summary>Rotação: o token atual vira o anterior da cadeia e é revogado no mesmo passo.</summary>
    public void MarcarSubstituidoPor(Guid novoTokenId, DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));

        if (EstaRevogado)
        {
            throw new TransicaoInvalidaException("Um token já revogado não pode ser rotacionado.");
        }

        SubstituidoPorId = novoTokenId;
        RevogadoEmUtc = agora;
    }
}
