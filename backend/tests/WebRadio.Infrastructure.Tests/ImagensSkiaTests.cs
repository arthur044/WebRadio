using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Erros;
using WebRadio.Infrastructure.Imagens;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>E3-F06 / S-B10 / E3-D4: o validador de cabeçalho (sem decodificar) e a recodificação para WebP sem metadado.</summary>
public class ValidadorDeCabecalhoSkiaTests
{
    private readonly ValidadorDeCabecalhoSkia _v = new();

    [Fact]
    public void Jpeg_e_png_validos_sao_aceitos_com_as_dimensoes_do_cabecalho()
    {
        var jpeg = _v.Validar(ImagensDeTeste.Jpeg(50, 30));
        var png = _v.Validar(ImagensDeTeste.Png(60, 40));

        Assert.True(jpeg.Aceita);
        Assert.Equal((50, 30), (jpeg.Largura, jpeg.Altura));
        Assert.True(png.Aceita);
        Assert.Equal((60, 40), (png.Largura, png.Altura));
    }

    public static TheoryData<string, byte[]> Recusadas() => new()
    {
        { "WebP", ImagensDeTeste.Webp() },
        { "GIF", ImagensDeTeste.Gif() },
        { "SVG", ImagensDeTeste.Svg() },
        { "HTML", ImagensDeTeste.Html() },
        { "vazio", [] },
        { "um byte", [0xFF] },
    };

    [Theory]
    [MemberData(nameof(Recusadas))]
    public void Formato_que_nao_e_jpeg_nem_png_e_recusado_pelos_magic_bytes(string caso, byte[] bytes)
    {
        var c = _v.Validar(bytes);

        Assert.False(c.Aceita, caso);
        Assert.Equal(MotivoDaImagemRecusada.FormatoNaoAceito, c.Motivo);
    }

    [Fact]
    public void Png_animado_e_recusado_por_ter_mais_de_um_quadro()
    {
        var c = _v.Validar(ImagensDeTeste.PngAnimado());

        Assert.False(c.Aceita);
        Assert.Equal(MotivoDaImagemRecusada.Animada, c.Motivo);
    }

    [Theory]
    [InlineData(4097, 10)]
    [InlineData(10, 4097)]
    [InlineData(10000, 10000)]
    public void Bomba_de_pixel_poucos_bytes_e_cabecalho_enorme_e_recusada_sem_decodificar(int largura, int altura)
    {
        var bomba = ImagensDeTeste.PngComDimensoesNoCabecalho(largura, altura);
        Assert.True(bomba.Length < 1000);

        var c = _v.Validar(bomba);

        Assert.False(c.Aceita);
        Assert.Equal(MotivoDaImagemRecusada.Dimensoes, c.Motivo);
    }

    [Fact]
    public void Lado_de_4096_px_exatos_e_aceito()
    {
        Assert.True(_v.Validar(ImagensDeTeste.PngComDimensoesNoCabecalho(4096, 4096)).Aceita);
    }

    [Fact]
    public void Magic_bytes_de_jpeg_com_lixo_depois_e_ilegivel_nao_excecao()
    {
        var c = _v.Validar([0xFF, 0xD8, 0xFF, 0x00, 0x01, 0x02]);

        Assert.False(c.Aceita);
        Assert.Equal(MotivoDaImagemRecusada.Ilegivel, c.Motivo);
    }
}

public class RecodificadorSkiaTests
{
    private static readonly byte[] AssinaturaRiff = "RIFF"u8.ToArray();

    private static async Task<byte[]> Recodificar(byte[] origem) => await new RecodificadorSkia().RecodificarParaWebpAsync(origem, default);

    [Fact]
    public async Task Jpeg_com_GPS_e_EXIF_sai_WebP_sem_nenhum_metadado()
    {
        var comGps = ImagensDeTeste.JpegComExif(ImagensDeTeste.Jpeg(), orientacao: 1, gps: true);
        Assert.True(comGps.AsSpan().IndexOf("Exif"u8) >= 0); // sanidade: o EXIF entrou no arquivo de origem

        var saida = await Recodificar(comGps);

        Assert.Equal(AssinaturaRiff, saida[..4]);
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(saida, 8, 4));
        var tipos = ImagensDeTeste.ChunksDoWebp(saida);
        Assert.DoesNotContain("EXIF", tipos);
        Assert.DoesNotContain("XMP ", tipos);
        Assert.DoesNotContain("ICCP", tipos);
        Assert.False(saida.AsSpan().IndexOf("Exif"u8) >= 0);
    }

    [Fact]
    public async Task Orientacao_6_do_EXIF_gira_a_imagem_de_40x20_para_20x40()
    {
        var origem = ImagensDeTeste.JpegComExif(ImagensDeTeste.Jpeg(40, 20), orientacao: 6, gps: false);

        var saida = await Recodificar(origem);

        Assert.Equal((20, 40), ImagensDeTeste.Dimensoes(saida));
    }

    [Theory]
    [InlineData(1, 40, 20)]
    [InlineData(2, 40, 20)]
    [InlineData(3, 40, 20)]
    [InlineData(4, 40, 20)]
    [InlineData(5, 20, 40)]
    [InlineData(6, 20, 40)]
    [InlineData(7, 20, 40)]
    [InlineData(8, 20, 40)]
    public async Task Cada_orientacao_do_EXIF_resulta_nas_dimensoes_certas(ushort orientacao, int larguraEsperada, int alturaEsperada)
    {
        var saida = await Recodificar(ImagensDeTeste.JpegComExif(ImagensDeTeste.Jpeg(40, 20), orientacao, gps: false));

        Assert.Equal((larguraEsperada, alturaEsperada), ImagensDeTeste.Dimensoes(saida));
    }

    [Fact]
    public async Task Png_com_transparencia_vira_WebP_legivel()
    {
        var saida = await Recodificar(ImagensDeTeste.Png(30, 30));

        Assert.Equal((30, 30), ImagensDeTeste.Dimensoes(saida));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(saida, 8, 4));
    }

    [Fact]
    public async Task Imagem_truncada_nao_vira_WebP_lanca()
    {
        var jpeg = ImagensDeTeste.Jpeg(200, 200);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Recodificar(jpeg[..(jpeg.Length / 2)]));
    }

    [Fact]
    public async Task Estourou_o_teto_de_tempo_responde_ocupado_e_a_vaga_segue_presa_ate_a_tarefa_abandonada_terminar()
    {
        var solta = new ManualResetEventSlim();
        var r = new RecodificadorSkia(TimeSpan.FromMilliseconds(300)) { Trabalho = _ => { solta.Wait(); return [1, 2, 3]; } };

        await Assert.ThrowsAsync<ServidorOcupadoException>(() => r.RecodificarParaWebpAsync([1], default)); // estourou o teto
        // A tarefa abandonada ainda segura a vaga: o próximo também leva 503, sem nem começar.
        await Assert.ThrowsAsync<ServidorOcupadoException>(() => r.RecodificarParaWebpAsync([1], default));

        solta.Set();
        await Task.Delay(100);
        Assert.Equal([1, 2, 3], await r.RecodificarParaWebpAsync([1], default)); // terminou: a vaga voltou
    }

    [Fact]
    public async Task Uma_de_cada_vez_o_segundo_espera_a_vaga_do_primeiro_dentro_do_teto()
    {
        var emAndamento = 0;
        var maximo = 0;
        var r = new RecodificadorSkia(TimeSpan.FromSeconds(5))
        {
            Trabalho = o =>
            {
                var agora = Interlocked.Increment(ref emAndamento);
                maximo = Math.Max(maximo, agora);
                Thread.Sleep(100);
                Interlocked.Decrement(ref emAndamento);
                return o;
            },
        };

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => r.RecodificarParaWebpAsync([9], default)));

        Assert.Equal(1, maximo);
    }

    [Fact]
    public async Task Cliente_que_desiste_antes_da_vaga_cancela_sem_travar_o_recodificador()
    {
        var solta = new ManualResetEventSlim();
        var r = new RecodificadorSkia(TimeSpan.FromSeconds(5)) { Trabalho = o => { solta.Wait(); return o; } };
        var primeiro = r.RecodificarParaWebpAsync([1], default);
        using var cancela = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.RecodificarParaWebpAsync([2], cancela.Token));

        solta.Set();
        await primeiro;
        Assert.Equal([3], await r.RecodificarParaWebpAsync([3], default));
    }
}
