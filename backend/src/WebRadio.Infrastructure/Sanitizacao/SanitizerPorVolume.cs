using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Sanitizacao;

/// <summary>Seção <c>Sanitizer</c>: o volume tmpfs compartilhado com o contêiner do sanitizer (08 E4-D1).</summary>
public sealed class SanitizerOptions
{
    public const string Secao = "Sanitizer";

    /// <summary><c>Sanitizer__DiretorioDeJobs</c>: o volume <c>sanitizer-jobs</c> (1 GB, uid 65532, modo 0700).</summary>
    public string DiretorioDeJobs { get; set; } = "/jobs";

    /// <summary>Folga sobre o <c>timeoutSegundos</c> do job antes de dar o sanitizer por morto (E4-D1: +15 s).</summary>
    public TimeSpan FolgaDoTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan IntervaloDeLeitura { get; set; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// E4-F05 (E4-D1, S-B09): o protocolo Worker ↔ sanitizer por VOLUME (o sanitizer não tem rede). Cada job é um diretório <c>{guid}</c>: o Worker grava o <c>in.bin</c> (download direto),
/// o <c>job.json</c> e, por último, o <c>pronto</c>; o sanitizer devolve <c>out.mp3</c> e <c>result.json</c> (por <c>rename</c>). Um sanitizer comprometido é quem escreve o resultado, então
/// aqui ele é DADO NÃO CONFIÁVEL: leitura única, só arquivo regular (nunca symlink, conferido antes e depois de abrir), tetos de tamanho (<c>result.json</c> ≤ 4 KB,
/// <c>out.mp3</c> ≤ o limite do tipo), JSON estrito e <c>motivo</c>/<c>codec</c> de formato fechado. O que não passar é <see cref="MotivoDaSanitizacao.ResultadoInvalido"/> (falha de processamento).
/// </summary>
public sealed partial class SanitizerPorVolume(SanitizerOptions opcoes, ILogger<SanitizerPorVolume> logger) : IAudioSanitizer
{
    public const int TetoDoResultadoBytes = 4096;

    public async Task<IJobDeSanitizacao> CriarJobAsync(Guid midiaId, CancellationToken ct = default)
    {
        var raiz = opcoes.DiretorioDeJobs;
        var diretorio = Path.Combine(raiz, midiaId.ToString("D"));
        // Um job anterior da MESMA mídia (nova tentativa) pode ter sobrado se o Worker morreu: começa de um diretório vazio.
        if (Directory.Exists(diretorio)) Directory.Delete(diretorio, recursive: true);
        if (!OperatingSystem.IsWindows())
            Directory.CreateDirectory(diretorio, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
            Directory.CreateDirectory(diretorio);
        await Task.CompletedTask;
        return new Job(diretorio, opcoes, logger);
    }

    [GeneratedRegex("^[a-z0-9_]{1,24}$")]
    private static partial Regex CodecValido();

    private sealed class Job(string diretorio, SanitizerOptions opcoes, ILogger logger) : IJobDeSanitizacao
    {
        private string Caminho(string nome) => Path.Combine(diretorio, nome);

        public Stream AbrirOriginalParaEscrita() => new FileStream(Caminho("in.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        public Stream AbrirOriginalParaLeitura() => new FileStream(Caminho("in.bin"), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        public async Task<ResultadoSanitizacao> ExecutarAsync(FormatoDeAudio formato, long duracaoMaximaSegundos, long tamanhoMaximoDaSaidaBytes, TimeSpan timeoutDoSanitizer, CancellationToken ct = default)
        {
            var segundos = Math.Max(1, (int)Math.Ceiling(timeoutDoSanitizer.TotalSeconds));
            // EXATAMENTE este formato de uma linha: o sanitizer o valida por regex ancorada (nada do que o Worker escreve é interpolado em comando).
            var json = string.Create(CultureInfo.InvariantCulture,
                $"{{\"formato\":\"{NomeDoFormato(formato)}\",\"duracaoMaxSegundos\":{duracaoMaximaSegundos},\"tamanhoMaxSaidaBytes\":{tamanhoMaximoDaSaidaBytes},\"timeoutSegundos\":{segundos}}}\n");
            await File.WriteAllTextAsync(Caminho("job.json"), json, ct);
            await File.WriteAllBytesAsync(Caminho("pronto"), [], ct); // por último: o sanitizer só olha diretórios com `pronto`

            var prazo = timeoutDoSanitizer + opcoes.FolgaDoTimeout;
            var inicio = DateTime.UtcNow;
            while (!File.Exists(Caminho("result.json")))
            {
                if (DateTime.UtcNow - inicio > prazo) return Falha(MotivoDaSanitizacao.Timeout);
                await Task.Delay(opcoes.IntervaloDeLeitura, ct);
            }

            return Ler(tamanhoMaximoDaSaidaBytes);
        }

        private ResultadoSanitizacao Ler(long tamanhoMaximoDaSaida)
        {
            var bytes = LerArquivoRegular(Caminho("result.json"), TetoDoResultadoBytes);
            if (bytes is null) return Falha(MotivoDaSanitizacao.ResultadoInvalido);

            bool ok;
            string? motivoTexto = null;
            decimal? duracao = null;
            int streams;
            string? codec;
            try
            {
                using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
                var raiz = doc.RootElement;
                if (raiz.ValueKind != JsonValueKind.Object) return Falha(MotivoDaSanitizacao.ResultadoInvalido);
                ok = raiz.GetProperty("ok").GetBoolean();
                streams = raiz.GetProperty("streamsAudio").GetInt32();
                codec = raiz.GetProperty("codec").GetString();
                if (raiz.TryGetProperty("motivo", out var m)) motivoTexto = m.GetString();
                if (raiz.TryGetProperty("duracaoSegundos", out var d)) duracao = d.GetDecimal();
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                return Falha(MotivoDaSanitizacao.ResultadoInvalido);
            }

            if (streams is < 0 or > 64 || (!string.IsNullOrEmpty(codec) && !CodecValido().IsMatch(codec))) return Falha(MotivoDaSanitizacao.ResultadoInvalido);
            codec = string.IsNullOrEmpty(codec) ? null : codec;

            if (!ok)
            {
                return Motivo(motivoTexto) is { } motivo
                    ? new ResultadoSanitizacao(false, motivo, null, null, streams, codec)
                    : Falha(MotivoDaSanitizacao.ResultadoInvalido);
            }

            if (duracao is null or <= 0 or > 100_000) return Falha(MotivoDaSanitizacao.ResultadoInvalido);
            var saida = LerArquivoRegular(Caminho("out.mp3"), tamanhoMaximoDaSaida);
            if (saida is null || saida.Length == 0) return Falha(MotivoDaSanitizacao.ResultadoInvalido);
            return new ResultadoSanitizacao(true, null, saida, duracao, streams, codec);
        }

        /// <summary>
        /// Lê o arquivo UMA vez: só arquivo regular (nunca symlink: conferido antes e depois de abrir), no máximo <paramref name="teto"/> bytes. Acima do teto = <c>null</c>.
        /// </summary>
        private static byte[]? LerArquivoRegular(string caminho, long teto)
        {
            try
            {
                if (new FileInfo(caminho).LinkTarget is not null) return null;
                using var handle = File.OpenHandle(caminho, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);
                if (new FileInfo(caminho).LinkTarget is not null) return null;
                var tamanho = RandomAccess.GetLength(handle);
                if (tamanho > teto) return null;
                var buffer = new byte[tamanho];
                var lidos = 0;
                while (lidos < tamanho)
                {
                    var n = RandomAccess.Read(handle, buffer.AsSpan(lidos), lidos);
                    if (n == 0) break;
                    lidos += n;
                }

                return lidos == tamanho ? buffer : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static MotivoDaSanitizacao? Motivo(string? texto) => texto switch
        {
            "job_invalido" => MotivoDaSanitizacao.JobInvalido,
            "formato_invalido" => MotivoDaSanitizacao.FormatoInvalido,
            "streams_audio_invalido" => MotivoDaSanitizacao.StreamsAudioInvalido,
            "stream_nao_audio" => MotivoDaSanitizacao.StreamNaoAudio,
            "codec_nao_permitido" => MotivoDaSanitizacao.CodecNaoPermitido,
            "falha_na_decodificacao" => MotivoDaSanitizacao.FalhaNaDecodificacao,
            "saida_grande_demais" => MotivoDaSanitizacao.SaidaGrandeDemais,
            "timeout" => MotivoDaSanitizacao.Timeout,
            _ => null,
        };

        private static ResultadoSanitizacao Falha(MotivoDaSanitizacao motivo) => new(false, motivo, null, null, 0, null);

        private static string NomeDoFormato(FormatoDeAudio f) => f switch
        {
            FormatoDeAudio.Mp3 => "mp3",
            FormatoDeAudio.Ogg => "ogg",
            FormatoDeAudio.Wav => "wav",
            FormatoDeAudio.Flac => "flac",
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(diretorio, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Um arquivo que o sanitizer ainda segura (ou que ele criou com outro dono): o próximo CriarJob da mesma mídia, ou o boot, limpa.
                logger.LogWarning("SANITIZER_JOB_NAO_APAGADO ({Tipo})", e.GetType().Name);
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// "Health Degraded" do Worker (E4-D7/E4-D13): arquivo-marcador com o motivo (o operador e o smoke o enxergam) e um log <c>Error</c> só na mudança de estado. O healthcheck do
/// contêiner continua sendo o heartbeat.
/// </summary>
public sealed class MarcadorDeDegradacao(string? arquivo, ILogger<MarcadorDeDegradacao> logger) : IMarcadorDeDegradacao
{
    private int _degradado;

    public void Degradado(string motivo)
    {
        if (Interlocked.Exchange(ref _degradado, 1) == 0)
            logger.LogError("WORKER_DEGRADADO: {Motivo}. A fila de mídia está parada até o antivírus voltar (arquivos continuam Pendente).", motivo);
        Escrever(motivo);
    }

    public void Normal()
    {
        if (Interlocked.Exchange(ref _degradado, 0) == 1) logger.LogInformation("WORKER_DEGRADADO_FIM: o antivírus voltou; a fila de mídia segue.");
        Apagar();
    }

    private void Escrever(string motivo)
    {
        if (arquivo is null) return;
        try { File.WriteAllText(arquivo, motivo); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* o log acima já registrou */ }
    }

    private void Apagar()
    {
        if (arquivo is null) return;
        try { File.Delete(arquivo); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* idem */ }
    }
}
