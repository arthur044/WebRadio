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
/// <see cref="Timeout"/>, <see cref="ResultadoInvalido"/> e <see cref="JobInvalido"/> são falha de PROCESSAMENTO (contam tentativa, E4-D13); todos os outros, inclusive
/// <see cref="SaidaGrandeDemais"/> (a saída bateu no -fs: música cortada), são do arquivo (rejeição determinística).
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

    /// <summary>
    /// Apaga os diretórios de job mais velhos que <paramref name="idadeMinima"/>: um Worker morto no meio deixa o <c>in.bin</c> (o original hostil) no tmpfs, e reiniciar o contêiner não
    /// esvazia o volume. Só o líder chama, com nenhum job em curso. Devolve quantos apagou.
    /// </summary>
    int LimparJobsAbandonados(TimeSpan idadeMinima);
}

public interface IJobDeSanitizacao : IAsyncDisposable
{
    /// <summary>Cria o <c>in.bin</c> (arquivo novo, sem seguir symlink) para o download.</summary>
    Stream AbrirOriginalParaEscrita();

    /// <summary>Abre o <c>in.bin</c> para leitura (hash, magic bytes e antivírus leem o mesmo arquivo).</summary>
    Stream AbrirOriginalParaLeitura();

    /// <summary>
    /// Escreve <c>job.json</c> e <c>pronto</c>, espera o sanitizer pegar o job (<c>iniciado</c>; não pegou no prazo: <see cref="SanitizerNaoIniciouException"/>, infraestrutura), espera o
    /// <c>result.json</c> até <paramref name="timeoutDoSanitizer"/> + 15 s CONTADOS DO <c>iniciado</c> e lê <c>out.mp3</c> e <c>result.json</c> sem seguir symlink e sem bloquear em FIFO,
    /// uma vez só, com teto (<c>result.json</c> ≤ 4 KB, <c>out.mp3</c> ≤ o limite de saída). Passou do prazo: <see cref="MotivoDaSanitizacao.Timeout"/>.
    /// </summary>
    Task<ResultadoSanitizacao> ExecutarAsync(FormatoDeAudio formato, long duracaoMaximaSegundos, long tamanhoMaximoDaSaidaBytes, TimeSpan timeoutDoSanitizer, CancellationToken ct = default);
}

/// <summary>
/// Antivírus sobre o arquivo original, antes do sanitizer (S-A02). Falha ou timeout nunca vira <see cref="ResultadoScan.Limpo"/>.
/// </summary>
public interface IMalwareScanner
{
    Task<ResultadoScan> EscanearAsync(Stream conteudo, CancellationToken ct = default);

    /// <summary>
    /// Sonda de disponibilidade que o Worker faz a CADA ciclo, ANTES de capturar item ou baixar arquivo (E4-D13): <c>null</c> = pronto; senão o código fechado de
    /// <see cref="MotivosDeDegradacao"/> (<c>scanner</c>: o antivírus não responde; <c>assinatura_velha</c>: a base passou da idade máxima ou não traz data). Com o antivírus fora,
    /// nenhum item é capturado e nada é baixado.
    /// </summary>
    Task<string?> ConferirDisponibilidadeAsync(CancellationToken ct = default);
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
public sealed class ScannerIndisponivelException(string motivo, Exception? inner = null, string codigo = MotivosDeDegradacao.Scanner) : Exception($"Antivírus indisponível: {motivo}", inner)
{
    public string Motivo { get; } = motivo;

    /// <summary>Um de <see cref="MotivosDeDegradacao"/>: é o que vai ao marcador <c>WORKER_DEGRADADO motivo=</c> (nunca o texto livre).</summary>
    public string Codigo { get; } = codigo;
}

/// <summary>Lista FECHADA de <c>motivo</c> do marcador <c>WORKER_DEGRADADO</c> (08 E4-D13).</summary>
public static class MotivosDeDegradacao
{
    /// <summary><c>clamd</c> recusa a conexão ou não responde.</summary>
    public const string Scanner = "scanner";

    /// <summary>Base de assinaturas com mais de <c>Seguranca:IdadeMaximaAssinaturaDias</c>, ou sem data legível (E4-D7).</summary>
    public const string AssinaturaVelha = "assinatura_velha";

    /// <summary>A limpeza de <c>midia/</c> abortou: a consulta falhou ou apagaria objetos demais (E4-D11, F09). Recuperado só pela rodada diária de <c>midia/</c>.</summary>
    public const string TravaApagao = "trava_apagao";

    /// <summary>A varredura de 30 min da quarentena abortou (E4-D11, v1.7, #153): consulta falhou ou passaria do limite próprio. Recuperado só por ela.</summary>
    public const string TravaApagaoQuarentena = "trava_apagao_quarentena";

    /// <summary>O sanitizer não pegou o job no prazo, ou o diretório de jobs está inacessível (E4-D13, #138).</summary>
    public const string Sanitizer = "sanitizer";
}

/// <summary>
/// O sanitizer não pegou o job (não escreveu <c>iniciado</c> no prazo): falha de INFRAESTRUTURA (E4-D13), nunca do arquivo. O item volta a <c>Pendente</c> com a tentativa devolvida.
/// </summary>
public sealed class SanitizerNaoIniciouException() : Exception("O sanitizer não iniciou o job no prazo.");

/// <summary>O diretório de jobs do sanitizer (volume compartilhado) está inacessível ao criar o job: falha de INFRAESTRUTURA (E4-D13, motivo <c>sanitizer</c>), nunca do arquivo.</summary>
public sealed class DiretorioDeJobsInacessivelException(Exception inner) : Exception("O diretório de jobs do sanitizer está inacessível.", inner);

/// <summary>
/// Falha atribuível ao ARQUIVO (E4-D13): o <c>clamd</c> caiu ou deu erro no meio do <c>INSTREAM</c>, estourou o tempo do scan ou recusou o tamanho. Conta como
/// tentativa; a 3ª vira <c>Rejeitado</c>, para um arquivo que derruba o <c>clamd</c> não voltar para a fila para sempre.
/// </summary>
public sealed class FalhaNoScanException(string motivo, Exception? inner = null) : Exception($"Falha no scan do arquivo: {motivo}", inner)
{
    public string Motivo { get; } = motivo;
}
