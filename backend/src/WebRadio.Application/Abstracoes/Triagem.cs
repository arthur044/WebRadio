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

/// <summary>
/// Falha de INFRAESTRUTURA do antivírus (E4-D13): <c>clamd</c> recusou a conexão ou não responde, ou a assinatura está velha (S-A02 item 6). O item volta a
/// <c>Pendente</c> e a tentativa é devolvida: um scanner parado nunca rejeita arquivo legítimo nem aprova sem scan.
/// </summary>
public sealed class ScannerIndisponivelException(string motivo, Exception? inner = null) : Exception($"Antivírus indisponível: {motivo}", inner)
{
    public string Motivo { get; } = motivo;
}

/// <summary>
/// Falha atribuível ao ARQUIVO (E4-D13): o <c>clamd</c> caiu ou deu erro no meio do <c>INSTREAM</c>, estourou o tempo do scan ou recusou o tamanho. Conta como
/// tentativa; a 3ª vira <c>Rejeitado</c>, para um arquivo que derruba o <c>clamd</c> não voltar para a fila para sempre.
/// </summary>
public sealed class FalhaNoScanException(string motivo, Exception? inner = null) : Exception($"Falha no scan do arquivo: {motivo}", inner)
{
    public string Motivo { get; } = motivo;
}
