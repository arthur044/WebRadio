using WebRadio.Infrastructure.Tests.Suporte;

namespace WebRadio.Api.IntegrationTests;

/// <summary>As imagens dos testes de upload: um JPEG de verdade, um com EXIF de GPS e os casos que têm de ser recusados.</summary>
public static class ImagensHttp
{
    public static byte[] Jpeg() => ImagensDeTeste.Jpeg(64, 32);

    public static byte[] JpegComGps() => ImagensDeTeste.JpegComExif(ImagensDeTeste.Jpeg(64, 32), orientacao: 1, gps: true);

    public static byte[] Html() => ImagensDeTeste.Html();

    public static byte[] Caso(string caso) => caso switch
    {
        "webp" => ImagensDeTeste.Webp(),
        "gif" => ImagensDeTeste.Gif(),
        "svg" => ImagensDeTeste.Svg(),
        "html" => ImagensDeTeste.Html(),
        "apng" => ImagensDeTeste.PngAnimado(),
        "bomba" => ImagensDeTeste.PngComDimensoesNoCabecalho(10000, 10000),
        "vazio" => [],
        _ => throw new ArgumentOutOfRangeException(nameof(caso)),
    };
}
