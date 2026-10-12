namespace WebRadio.Application.Abstracoes;

public sealed record ResultadoRotacao(bool Sucesso, Guid UsuarioId, RefreshOpaco? Novo, bool Concorrente = false)
{
    public static ResultadoRotacao Invalido { get; } = new(false, Guid.Empty, null);

    /// <summary>Outra aba já rotacionou o sucessor dentro da janela: a sessão é válida e o cookie novo já foi gravado por ela.</summary>
    public static ResultadoRotacao PerdeuCorrida { get; } = new(false, Guid.Empty, null, Concorrente: true);
}

/// <summary>Refresh opaco rotativo (02 §2, 05 §6, S-M10, S-M11).</summary>
public interface IRefreshTokenService
{
    /// <summary>
    /// Rotação atômica: só uma de duas rotações concorrentes consome o token. Reuso dentro da janela de 10 s devolve o
    /// MESMO sucessor; reuso depois da janela revoga a família inteira (sinal de roubo).
    /// </summary>
    Task<ResultadoRotacao> RotacionarAsync(string tokenBruto, byte[] ipHash, CancellationToken ct);

    /// <summary>Logout: revoga a família do token apresentado. Token desconhecido é no-op.</summary>
    Task RevogarFamiliaAsync(string tokenBruto, CancellationToken ct);

    /// <summary>S-M11: troca de senha, papel e desativação. <paramref name="manterFamiliaDoToken"/> preserva a sessão atual.</summary>
    Task RevogarTodasDoUsuarioAsync(Guid usuarioId, string? manterFamiliaDoToken, CancellationToken ct);
}

/// <summary>Seeders de boot (AdminSeeder, DevDataSeeder). Idempotentes: rodar duas vezes não duplica nada.</summary>
public interface IInicializadorDeDados
{
    Task ExecutarAsync(CancellationToken ct);
}
