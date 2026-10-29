using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace WebRadio.Infrastructure.Tests.Suporte;

/// <summary>Imagens de fixture montadas à mão (bytes), sem arquivo no repositório: JPEG com EXIF, PNG com IHDR trocado, APNG, GIF, SVG...</summary>
public static class ImagensDeTeste
{
    public static byte[] Jpeg(int largura = 40, int altura = 20) => Codificar(largura, altura, SKEncodedImageFormat.Jpeg);

    public static byte[] Png(int largura = 40, int altura = 20) => Codificar(largura, altura, SKEncodedImageFormat.Png);

    public static byte[] Webp(int largura = 40, int altura = 20) => Codificar(largura, altura, SKEncodedImageFormat.Webp);

    public static byte[] Gif() => Encoding.ASCII.GetBytes("GIF89a\x01\x00\x01\x00\x80\x00\x00\xff\xff\xff\x00\x00\x00;");

    public static byte[] Svg() => Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><script>alert(1)</script></svg>");

    public static byte[] Html() => Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");

    private static byte[] Codificar(int largura, int altura, SKEncodedImageFormat formato)
    {
        using var bitmap = new SKBitmap(largura, altura);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var tinta = new SKPaint { Color = new SKColor(200, 30, 30) };
            // Marca assimétrica (um retângulo vermelho no canto de cima, à esquerda) para a orientação do EXIF ser visível no resultado.
            canvas.DrawRect(0, 0, Math.Max(1, largura / 4f), Math.Max(1, altura / 2f), tinta);
        }

        using var imagem = SKImage.FromBitmap(bitmap);
        using var dados = imagem.Encode(formato, 90);
        return dados.ToArray();
    }

    /// <summary>
    /// Insere um APP1/EXIF logo depois do SOI com Orientation e, se pedido, um GPS IFD (latitude norte): o que um celular grava e o que a
    /// recodificação tem de jogar fora.
    /// </summary>
    public static byte[] JpegComExif(byte[] jpeg, ushort orientacao, bool gps)
    {
        using var tiff = new MemoryStream();
        void W16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); tiff.Write(b); }
        void W32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); tiff.Write(b); }

        tiff.Write("II"u8);
        W16(42);
        W32(8);                       // IFD0 logo depois do cabeçalho TIFF
        var entradas = gps ? 2 : 1;
        W16((ushort)entradas);
        W16(0x0112); W16(3); W32(1); W16(orientacao); W16(0);          // Orientation (SHORT)
        if (gps) { W16(0x8825); W16(4); W32(1); W32((uint)(8 + 2 + 12 * entradas + 4)); } // GPSInfo -> GPS IFD
        W32(0);                       // sem IFD1
        if (gps)
        {
            W16(1);                   // um tag
            W16(0x0001); W16(2); W32(2); tiff.Write("N\0\0\0"u8); // GPSLatitudeRef = "N"
            W32(0);
        }

        var exif = new MemoryStream();
        exif.Write("Exif\0\0"u8);
        exif.Write(tiff.ToArray());
        var carga = exif.ToArray();

        using var saida = new MemoryStream();
        saida.Write(jpeg.AsSpan(0, 2));                                  // SOI
        saida.WriteByte(0xFF); saida.WriteByte(0xE1);                    // APP1
        Span<byte> tamanho = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(tamanho, (ushort)(carga.Length + 2));
        saida.Write(tamanho);
        saida.Write(carga);
        saida.Write(jpeg.AsSpan(2));
        return saida.ToArray();
    }

    /// <summary>PNG de verdade (pequeno) com o IHDR trocado para outra dimensão: a "bomba de pixel" (poucos bytes, cabeçalho enorme).</summary>
    public static byte[] PngComDimensoesNoCabecalho(int largura, int altura)
    {
        var png = Png(8, 8);
        // Assinatura (8) + tamanho (4) + "IHDR" (4) + dados (13) + CRC (4)
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), (uint)largura);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), (uint)altura);
        Crc(png.AsSpan(12, 17)).CopyTo(png, 29); // CRC de "IHDR" + 13 bytes
        return png;
    }

    /// <summary>PNG animado (APNG) mínimo: acTL com 2 quadros, fcTL + IDAT do primeiro e fcTL + fdAT do segundo.</summary>
    public static byte[] PngAnimado()
    {
        var png = Png(8, 8);
        var idat = Chunks(png).First(c => c.Tipo == "IDAT");
        var ihdr = Chunks(png).First(c => c.Tipo == "IHDR");

        using var saida = new MemoryStream();
        saida.Write(png.AsSpan(0, 8));
        saida.Write(Chunk("IHDR", ihdr.Dados));
        saida.Write(Chunk("acTL", [0, 0, 0, 2, 0, 0, 0, 0]));
        saida.Write(Chunk("fcTL", FrameControl(0)));
        saida.Write(Chunk("IDAT", idat.Dados));
        saida.Write(Chunk("fcTL", FrameControl(1)));
        saida.Write(Chunk("fdAT", [0, 0, 0, 2, .. idat.Dados]));
        saida.Write(Chunk("IEND", []));
        return saida.ToArray();
    }

    private static byte[] FrameControl(uint sequencia)
    {
        var b = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0), sequencia);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 8);   // largura
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 8);   // altura
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(20), 1);  // delay num
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(22), 10); // delay den
        return b;
    }

    private static byte[] Chunk(string tipo, byte[] dados)
    {
        var saida = new byte[12 + dados.Length];
        BinaryPrimitives.WriteUInt32BigEndian(saida.AsSpan(0), (uint)dados.Length);
        Encoding.ASCII.GetBytes(tipo).CopyTo(saida, 4);
        dados.CopyTo(saida, 8);
        Crc(saida.AsSpan(4, 4 + dados.Length)).CopyTo(saida, 8 + dados.Length);
        return saida;
    }

    /// <summary>CRC-32 do PNG (polinômio 0xEDB88320), em big-endian: o que fecha cada chunk.</summary>
    private static byte[] Crc(ReadOnlySpan<byte> dados)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in dados)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        var saida = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(saida, ~crc);
        return saida;
    }

    private static IEnumerable<(string Tipo, byte[] Dados)> Chunks(byte[] png)
    {
        var p = 8;
        while (p + 12 <= png.Length)
        {
            var tamanho = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p));
            yield return (Encoding.ASCII.GetString(png, p + 4, 4), png.AsSpan(p + 8, tamanho).ToArray());
            p += 12 + tamanho;
        }
    }

    /// <summary>Os tipos de chunk do contêiner RIFF do WebP (<c>VP8 </c>, <c>EXIF</c>, <c>XMP </c>, <c>ICCP</c>...), depois do cabeçalho de 12 bytes.</summary>
    public static IReadOnlyList<string> ChunksDoWebp(byte[] webp)
    {
        var tipos = new List<string>();
        var p = 12;
        while (p + 8 <= webp.Length)
        {
            tipos.Add(Encoding.ASCII.GetString(webp, p, 4));
            var tamanho = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(p + 4));
            p += 8 + tamanho + (tamanho & 1);
        }

        return tipos;
    }

    public static (int Largura, int Altura) Dimensoes(byte[] imagem)
    {
        using var dados = SKData.CreateCopy(imagem);
        using var codec = SKCodec.Create(dados)!;
        return (codec.Info.Width, codec.Info.Height);
    }
}
