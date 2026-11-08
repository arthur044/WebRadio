using WebRadio.Application.Abstracoes;

namespace WebRadio.Application.Features.Midias;

/// <summary>
/// S-C01 item 1: só os magic bytes decidem o demuxer. <c>ID3</c> ou frame sync (<c>0xFF</c> + 3 bits altos) → mp3; <c>OggS</c> → ogg; <c>RIFF....WAVE</c> → wav; <c>fLaC</c> → flac.
/// Qualquer outro início (<c>#EXTM3U</c>, <c>ffconcat</c>, HTML...) → <c>null</c> e o arquivo é rejeitado SEM chamar o ffprobe.
/// </summary>
public static class DetectorDeFormato
{
    public const int BytesNecessarios = 12;

    public static FormatoDeAudio? Detectar(ReadOnlySpan<byte> inicio)
    {
        if (inicio.Length >= 3 && inicio[0] == 'I' && inicio[1] == 'D' && inicio[2] == '3') return FormatoDeAudio.Mp3;
        if (inicio.Length >= 4 && inicio[0] == 'O' && inicio[1] == 'g' && inicio[2] == 'g' && inicio[3] == 'S') return FormatoDeAudio.Ogg;
        if (inicio.Length >= 4 && inicio[0] == 'f' && inicio[1] == 'L' && inicio[2] == 'a' && inicio[3] == 'C') return FormatoDeAudio.Flac;
        if (inicio.Length >= 12 && inicio[..4].SequenceEqual("RIFF"u8) && inicio[8..12].SequenceEqual("WAVE"u8)) return FormatoDeAudio.Wav;
        if (inicio.Length >= 2 && inicio[0] == 0xFF && (inicio[1] & 0xE0) == 0xE0) return FormatoDeAudio.Mp3;
        return null;
    }

    /// <summary>O <c>out.mp3</c> começa por <c>ID3</c> ou por um frame sync (a saída do sanitizer é a única coisa que entra na biblioteca).</summary>
    public static bool CabecalhoDeMp3(ReadOnlySpan<byte> saida) => Detectar(saida) == FormatoDeAudio.Mp3;

    /// <summary>
    /// Duração coerente com os bytes LIDOS (S-B09): a saída é CBR 192 kbps (24.000 bytes por segundo), 5% de tolerância mais 2 KB de cabeçalho (Xing/ID3). Um sanitizer comprometido
    /// que declara 10 s e entrega 200 MB, ou o contrário, não passa.
    /// </summary>
    public static bool DuracaoCoerenteComOsBytes(long bytesLidos, decimal duracaoSegundos)
    {
        var esperado = (double)duracaoSegundos * 24_000d;
        return Math.Abs(bytesLidos - esperado) <= esperado * 0.05 + 2048;
    }
}
