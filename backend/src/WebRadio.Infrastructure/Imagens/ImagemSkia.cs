using SkiaSharp;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Erros;

namespace WebRadio.Infrastructure.Imagens;

/// <summary>
/// Confere o cabeçalho em dois degraus (S-B10, 07 E3-D4): (1) os magic bytes em código gerenciado, antes de o Skia ver o arquivo
/// (<c>FF D8 FF</c> JPEG, <c>89 50 4E 47 0D 0A 1A 0A</c> PNG: WebP, GIF, SVG e HTML renomeado morrem aqui); (2) o <c>SKCodec</c> lê só o
/// cabeçalho: formato, <c>FrameCount == 1</c> e lado ≤ 4096 px, sem decodificar pixel.
/// </summary>
public sealed class ValidadorDeCabecalhoSkia : IValidadorDeCabecalhoDeImagem
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public CabecalhoDaImagem Validar(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var jpeg = bytes.AsSpan().StartsWith(Jpeg);
        var png = bytes.AsSpan().StartsWith(Png);
        if (!jpeg && !png) return CabecalhoDaImagem.Recusada(MotivoDaImagemRecusada.FormatoNaoAceito);
        // O codec do Skia (libpng) trata um APNG como imagem estática de um quadro: o chunk acTL, que vem antes do primeiro IDAT, é conferido aqui.
        if (png && TemChunkDeAnimacao(bytes)) return CabecalhoDaImagem.Recusada(MotivoDaImagemRecusada.Animada);

        try
        {
            using var dados = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(dados);
            if (codec is null) return CabecalhoDaImagem.Recusada(MotivoDaImagemRecusada.Ilegivel);

            // O formato que o Skia detectou tem de ser o dos magic bytes (JPEG ou PNG): nada de WebP/GIF dentro de um cabeçalho mentiroso.
            if (codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png))
                return CabecalhoDaImagem.Recusada(MotivoDaImagemRecusada.FormatoNaoAceito);
            if (codec.FrameCount > 1) return CabecalhoDaImagem.Recusada(MotivoDaImagemRecusada.Animada);

            var (largura, altura) = (codec.Info.Width, codec.Info.Height);
            if (largura <= 0 || altura <= 0) return CabecalhoDaImagem.Recusada(MotivoDaImagemRecusada.Ilegivel);
            if (largura > LimitesDaImagem.LadoMaximo || altura > LimitesDaImagem.LadoMaximo)
                return CabecalhoDaImagem.Recusada(MotivoDaImagemRecusada.Dimensoes);

            return new CabecalhoDaImagem(true, null, largura, altura);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return CabecalhoDaImagem.Recusada(MotivoDaImagemRecusada.Ilegivel);
        }
    }

    /// <summary>Percorre os chunks do PNG até o primeiro <c>IDAT</c> (ou o fim) à procura de <c>acTL</c>: loop limitado pelo tamanho do arquivo.</summary>
    private static bool TemChunkDeAnimacao(byte[] png)
    {
        var posicao = Png.Length;
        while (posicao + 8 <= png.Length)
        {
            var tamanho = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(posicao));
            var tipo = png.AsSpan(posicao + 4, 4);
            if (tipo.SequenceEqual("acTL"u8)) return true;
            if (tipo.SequenceEqual("IDAT"u8) || tipo.SequenceEqual("IEND"u8)) return false;
            var proximo = (long)posicao + 12 + tamanho;
            if (proximo > png.Length) return false;
            posicao = (int)proximo;
        }

        return false;
    }
}

/// <summary>
/// Decodifica (já sabendo, pelo cabeçalho, que é pequeno), aplica a orientação do EXIF, converte para sRGB (sem perfil ICC na saída),
/// descarta todo metadado e codifica WebP q85. Decode e encode sob <c>SemaphoreSlim(1)</c> (o decode de 4096×4096 pesa uns 64 MB), com
/// teto de <see cref="LimitesDaImagem.TempoMaximo"/>. O Skia não é cancelável: roda em tarefa separada; ao estourar, a requisição recebe 503
/// e a tarefa é ABANDONADA, e ela continua segurando a vaga até terminar de fato, então as requisições seguintes também recebem 503 até lá.
/// </summary>
public sealed class RecodificadorSkia : IRecodificadorDeImagem
{
    public const int QualidadeWebp = 85;

    private readonly SemaphoreSlim _vaga = new(1, 1);
    private readonly TimeSpan _teto;

    public RecodificadorSkia() : this(LimitesDaImagem.TempoMaximo)
    {
    }

    /// <summary>Teto menor para o teste de timeout.</summary>
    internal RecodificadorSkia(TimeSpan teto) => _teto = teto;

    /// <summary>Costura de teste: roda dentro da vaga, no lugar do trabalho real.</summary>
    internal Func<byte[], byte[]>? Trabalho { get; init; }

    public async Task<byte[]> RecodificarParaWebpAsync(byte[] origem, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origem);
        // Sem vaga: outro decode em andamento (ou abandonado). Não enfileira ninguém por mais que o teto.
        if (!await _vaga.WaitAsync(_teto, ct)) throw new ServidorOcupadoException();

        Task<byte[]> tarefa;
        try
        {
            // A vaga é solta SÓ quando o trabalho termina de verdade, mesmo que a requisição já tenha desistido.
            tarefa = Task.Run(() =>
            {
                try
                {
                    return Trabalho is { } t ? t(origem) : Converter(origem);
                }
                finally
                {
                    _vaga.Release();
                }
            }, CancellationToken.None);
        }
        catch
        {
            _vaga.Release();
            throw;
        }

        var terminou = await Task.WhenAny(tarefa, Task.Delay(_teto, ct));
        if (terminou != tarefa)
        {
            ct.ThrowIfCancellationRequested();
            _ = tarefa.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // observa a exceção da tarefa abandonada
            throw new ServidorOcupadoException();
        }

        return await tarefa;
    }

    private static byte[] Converter(byte[] origem)
    {
        using var dados = SKData.CreateCopy(origem);
        using var codec = SKCodec.Create(dados) ?? throw new ImagemIlegivelException("Imagem ilegível.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
        using var bruto = new SKBitmap(info);
        var resultado = codec.GetPixels(info, bruto.GetPixels());
        if (resultado is not SKCodecResult.Success)
            throw new ImagemIlegivelException(resultado == SKCodecResult.IncompleteInput ? "Imagem truncada." : "Não consegui decodificar a imagem.");

        // Sem giro (o caso comum) não faz cópia: um decode de 4096×4096 já pesa 64 MB, e o giro de um EXIF é o único motivo de um segundo bitmap.
        var girar = codec.EncodedOrigin is not (SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default);
        using var girada = girar ? Orientar(bruto, codec.EncodedOrigin) : null;
        var orientada = girada ?? bruto;
        // Os pixels já estão em sRGB (a conversão foi feita no decode). O pixmap SEM espaço de cor faz o encoder não escrever o chunk ICCP: a saída
        // não leva perfil nenhum, só os pixels (o WebP sem perfil é sRGB por definição).
        var semPerfil = orientada.Info.WithColorSpace(null);
        using var pixmap = new SKPixmap(semPerfil, orientada.GetPixels(), orientada.RowBytes);
        using var saida = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, QualidadeWebp))
            ?? throw new InvalidOperationException("Falha ao codificar WebP.");
        return saida.ToArray();
    }

    /// <summary>Aplica a orientação do EXIF (1 a 8) desenhando num bitmap novo (o chamador só chama com uma orientação que gira).</summary>
    internal static SKBitmap Orientar(SKBitmap origem, SKEncodedOrigin orientacao)
    {

        var troca = orientacao is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (w, h) = (origem.Width, origem.Height);
        var destino = new SKBitmap(new SKImageInfo(troca ? h : w, troca ? w : h, origem.ColorType, origem.AlphaType, origem.ColorSpace));
        using var canvas = new SKCanvas(destino);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(orientacao switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        });
        canvas.DrawBitmap(origem, 0, 0, new SKSamplingOptions(), null);
        return destino;
    }
}
