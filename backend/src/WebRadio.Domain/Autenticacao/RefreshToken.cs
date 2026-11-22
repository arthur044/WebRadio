using WebRadio.Domain.Common;
using WebRadio.Domain.Erros;

namespace WebRadio.Domain.Autenticacao;

/// <summary>
/// schema seg. Usar um token já revogado revoga toda a FamiliaId (sinal de roubo) — 01-dominio.md §2.2. A família tem um TETO absoluto de 30 dias desde o login
/// (<see cref="FamiliaCriadaEmUtc"/>, #18): o refresh desliza (7 dias a cada rotação), mas nunca passa do teto; um cookie roubado e rotacionado não vive para sempre.
/// </summary>
public sealed class RefreshToken : Entidade
{
    public const int TamanhoHashBytes = 32;

    /// <summary>Teto absoluto da família: <c>ExpiraEmUtc = min(criado + duração, FamiliaCriadaEmUtc + 30 dias)</c>. Passou do teto, a rotação é inválida (novo login).</summary>
    public static readonly TimeSpan TetoDaFamilia = TimeSpan.FromDays(30);

    public Guid UsuarioId { get; private set; }

    public byte[] TokenHash { get; private set; } = [];

    public Guid FamiliaId { get; private set; }

    public DateTime ExpiraEmUtc { get; private set; }

    public DateTime CriadoEmUtc { get; private set; }

    /// <summary>Quando o login abriu a família (o primeiro token dela); toda rotação herda. Linhas anteriores à coluna ficam com o início da família ou, na falta, o próprio <c>CriadoEmUtc</c>.</summary>
    public DateTime FamiliaCriadaEmUtc { get; private set; }

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
        byte[]? criadoPorIpHash,
        DateTime? familiaCriadaEmUtc = null)
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

        // O login abre a família (a data dela é a do próprio token); a rotação passa a do pai.
        var familiaCriada = familiaCriadaEmUtc ?? criadoEmUtc;
        UtcGuard.Exigir(familiaCriada, nameof(familiaCriadaEmUtc));
        if (familiaCriada > criadoEmUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(familiaCriadaEmUtc), "A família não pode ter nascido depois do token.");
        }

        var teto = familiaCriada.Add(TetoDaFamilia);
        if (criadoEmUtc >= teto)
        {
            throw new ArgumentOutOfRangeException(nameof(familiaCriadaEmUtc), "A família passou do teto absoluto: não há token a emitir (novo login).");
        }

        var expira = criadoEmUtc.Add(duracao);

        UsuarioId = usuarioId;
        TokenHash = (byte[])tokenHash.Clone();
        FamiliaId = familiaId;
        CriadoEmUtc = criadoEmUtc;
        FamiliaCriadaEmUtc = familiaCriada;
        ExpiraEmUtc = expira < teto ? expira : teto;
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
