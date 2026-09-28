namespace WebRadio.Domain.Midia;

/// <summary>
/// Limites por TipoMidia (01-dominio.md §2.5), configuráveis em `Midia:Limites` na Application/Infrastructure.
/// O Domain só aplica o limite recebido; não hardcoda os valores (Vinheta 10 MB/1-60s, Comercial 20 MB/5-120s,
/// Musica 250 MB/30s-20min).
/// </summary>
public readonly record struct LimiteMidia(long TamanhoMaximoBytes, TimeSpan DuracaoMinima, TimeSpan DuracaoMaxima);
