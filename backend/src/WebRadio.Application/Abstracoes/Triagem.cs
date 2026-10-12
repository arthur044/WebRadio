using WebRadio.Domain.Enums;

namespace WebRadio.Application.Abstracoes;

/// <summary>Recodifica o áudio enviado (S-C01). Só o contrato no Épico 1; a implementação (ffmpeg isolado) é do Épico 4.</summary>
public interface IAudioSanitizer
{
    Task<ResultadoSanitizacao> SanitizarAsync(Stream original, TipoMidia tipo, CancellationToken ct = default);
}

public sealed record ResultadoSanitizacao(bool Aprovado, string? MotivoRejeicao, Stream? Saida, int? DuracaoSegundos);

/// <summary>
/// Antivírus sobre o arquivo original, antes do sanitizer (S-A02). Falha ou timeout nunca vira <see cref="ResultadoScan.Limpo"/>.
/// </summary>
public interface IMalwareScanner
{
    Task<ResultadoScan> EscanearAsync(Stream conteudo, CancellationToken ct = default);
}

public enum ResultadoScan
{
    Limpo = 1,
    Infectado = 2,

    /// <summary>Scanner de dev (NoOp): nada foi verificado. Aparece no log como SCAN_IGNORADO.</summary>
    Ignorado = 3,
}
