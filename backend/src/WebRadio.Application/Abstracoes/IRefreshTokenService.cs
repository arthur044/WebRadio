using WebRadio.Domain.Usuarios;

namespace WebRadio.Application.Abstracoes;

/// <summary>Refresh tokens opacos de 256 bits; o banco guarda só o SHA-256 (02-api-rest §2, 05 §6).</summary>
public interface IRefreshTokenService
{
    /// <summary>Abre uma família nova (login).</summary>
    Task<RefreshEmitido> EmitirAsync(Usuario usuario, byte[]? ipHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rotação atômica (S-M10): só uma de duas rotações concorrentes vence. Reuso dentro da janela de 10 s devolve o
    /// mesmo sucessor; reuso depois da janela revoga a família inteira.
    /// </summary>
    Task<ResultadoRotacao> RotacionarAsync(string tokenBruto, byte[]? ipHash, CancellationToken cancellationToken = default);

    /// <summary>Logout: revoga a família do token apresentado. Token desconhecido é no-op.</summary>
    Task RevogarFamiliaAsync(string tokenBruto, CancellationToken cancellationToken = default);

    /// <summary>S-M11: troca de senha, papel e desativação. manterFamiliaDoToken preserva a sessão atual.</summary>
    Task RevogarTodasDoUsuarioAsync(Guid usuarioId, string? manterFamiliaDoToken, CancellationToken cancellationToken = default);
}

public sealed record RefreshEmitido(string TokenBruto, DateTime ExpiraEmUtc);

public sealed record ResultadoRotacao(bool Sucesso, Guid UsuarioId, RefreshEmitido? Novo, bool Concorrente = false)
{
    public static ResultadoRotacao Invalido { get; } = new(false, Guid.Empty, null);

    /// <summary>Outra aba ja rotacionou o sucessor dentro da janela: a sessao e valida e o cookie novo ja foi gravado por ela.</summary>
    public static ResultadoRotacao PerdeuCorrida { get; } = new(false, Guid.Empty, null, Concorrente: true);
}
