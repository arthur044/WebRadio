namespace WebRadio.Application.Abstracoes;

/// <summary>Formato do contêiner do original, decidido SÓ pelos magic bytes (S-C01 item 1): é o <c>-f</c> forçado do ffprobe e do ffmpeg.</summary>
public enum FormatoDeAudio
{
    Mp3 = 1,
    Ogg = 2,
    Wav = 3,
    Flac = 4,
}

/// <summary>
/// Códigos de <c>motivo</c> do sanitizer (lista fechada, 08 E4-D1). Nunca é texto do stderr do ffmpeg: o Worker troca o código pela mensagem fixa da biblioteca.
/// <see cref="Timeout"/> e <see cref="ResultadoInvalido"/> são falha de PROCESSAMENTO; <see cref="SaidaGrandeDemais"/> (a saída bateu no -fs: música cortada) é do arquivo. Os outros (contam tentativa, E4-D13); os demais são do arquivo (rejeição determinística).
/// </summary>
public enum MotivoDaSanitizacao
{
    JobInvalido = 1,
    FormatoInvalido = 2,
    StreamsAudioInvalido = 3,
    StreamNaoAudio = 4,
    CodecNaoPermitido = 5,
    FalhaNaDecodificacao = 6,
    Timeout = 7,
    ResultadoInvalido = 8,
    SaidaGrandeDemais = 9,
}

/// <summary>
/// Saída lida UMA vez do sanitizer (S-B09). <c>Saida</c> já está na memória (≤ limite do tipo); <c>DuracaoSegundos</c> é a do <c>ffprobe</c> da SAÍDA, e <c>Codec</c> é o do
/// original, já validado por regex (vai ao log).
/// </summary>
public sealed record ResultadoSanitizacao(bool Aprovado, MotivoDaSanitizacao? Motivo, byte[]? Saida, decimal? DuracaoSegundos, int StreamsAudio, string? Codec);

/// <summary>
/// Isolamento do ffmpeg (S-C01, 08 E4-D1): um job é um diretório no volume compartilhado com o contêiner <c>sanitizer</c> (sem rede, sem segredo). O Worker baixa o original
/// DIRETO no job (<see cref="AbrirOriginalParaEscrita"/>), depois chama <see cref="ExecutarAsync"/>; descartar o job apaga o diretório. Um job por vez.
/// </summary>
public interface IAudioSanitizer
{
    Task<IJobDeSanitizacao> CriarJobAsync(Guid midiaId, CancellationToken ct = default);
}

public interface IJobDeSanitizacao : IAsyncDisposable
{
    /// <summary>Cria o <c>in.bin</c> (arquivo novo, sem seguir symlink) para o download.</summary>
    Stream AbrirOriginalParaEscrita();

    /// <summary>Abre o <c>in.bin</c> para leitura (hash, magic bytes e antivírus leem o mesmo arquivo).</summary>
    Stream AbrirOriginalParaLeitura();

    /// <summary>
    /// Escreve <c>job.json</c> e <c>pronto</c>, espera o <c>result.json</c> até <paramref name="timeoutDoSanitizer"/> + 15 s e lê <c>out.mp3</c> e <c>result.json</c> sem seguir
    /// symlink, uma vez só, com teto (<c>result.json</c> ≤ 4 KB, <c>out.mp3</c> ≤ o limite de saída). Passou do prazo: <see cref="MotivoDaSanitizacao.Timeout"/>.
    /// </summary>
    Task<ResultadoSanitizacao> ExecutarAsync(FormatoDeAudio formato, long duracaoMaximaSegundos, long tamanhoMaximoDaSaidaBytes, TimeSpan timeoutDoSanitizer, CancellationToken ct = default);
}

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
