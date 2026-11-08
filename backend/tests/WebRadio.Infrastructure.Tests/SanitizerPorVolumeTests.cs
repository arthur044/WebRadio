using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Sanitizacao;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>E4-F05 (E4-D1, S-B09): o protocolo por volume e a leitura do resultado de um sanitizer que NÃO é confiável. Um "sanitizer" de mentira escreve no diretório do job.</summary>
public sealed class SanitizerPorVolumeTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "wr-san-" + Guid.NewGuid().ToString("N"));

    public SanitizerPorVolumeTests() => Directory.CreateDirectory(_raiz);

    public void Dispose()
    {
        try { Directory.Delete(_raiz, recursive: true); } catch (IOException) { }
    }

    private SanitizerPorVolume Novo(TimeSpan? folga = null) => new(
        new SanitizerOptions { DiretorioDeJobs = _raiz, FolgaDoTimeout = folga ?? TimeSpan.FromSeconds(15), IntervaloDeLeitura = TimeSpan.FromMilliseconds(20) },
        NullLogger<SanitizerPorVolume>.Instance);

    private static readonly byte[] Mp3 = [0xFF, 0xFB, 0x90, 0x00, 1, 2, 3, 4];

    /// <summary>O que o sanitizer faria: espera o `pronto`, devolve o que o teste mandar e escreve `result.json` por último.</summary>
    private static Task Sanitizer(string diretorio, Action<string> responder) => Task.Run(async () =>
    {
        for (var i = 0; i < 500 && !File.Exists(Path.Combine(diretorio, "pronto")); i++) await Task.Delay(10);
        responder(diretorio);
    });

    private static void Ok(string d, string? outMp3 = null, string result = """{"ok":true,"duracaoSegundos":12.5,"streamsAudio":1,"codec":"flac"}""")
    {
        File.WriteAllBytes(Path.Combine(d, "out.mp3"), outMp3 is null ? Mp3 : Encoding.ASCII.GetBytes(outMp3));
        File.WriteAllText(Path.Combine(d, "result.json"), result);
    }

    private async Task<(ResultadoSanitizacao R, string Dir)> Executar(Action<string> responder, long tamanhoMax = 1000, TimeSpan? timeout = null, TimeSpan? folga = null)
    {
        var id = Guid.NewGuid();
        await using var job = await Novo(folga).CriarJobAsync(id);
        var dir = Path.Combine(_raiz, id.ToString("D"));
        var sanitizer = Sanitizer(dir, responder);
        var r = await job.ExecutarAsync(FormatoDeAudio.Flac, 300, tamanhoMax, timeout ?? TimeSpan.FromSeconds(5));
        await sanitizer;
        return (r, dir);
    }

    [Fact]
    public async Task Escreve_job_json_numa_linha_no_formato_exato_e_o_pronto_por_ultimo()
    {
        var id = Guid.NewGuid();
        await using var job = await Novo().CriarJobAsync(id);
        var dir = Path.Combine(_raiz, id.ToString("D"));
        string? json = null;
        var ordem = new List<string>();
        var sanitizer = Task.Run(async () =>
        {
            for (var i = 0; i < 500 && !File.Exists(Path.Combine(dir, "pronto")); i++) await Task.Delay(10);
            ordem.AddRange(Directory.GetFiles(dir).Select(Path.GetFileName)!);
            json = File.ReadAllText(Path.Combine(dir, "job.json"));
            Ok(dir);
        });

        await job.ExecutarAsync(FormatoDeAudio.Ogg, 300, 262_144_000, TimeSpan.FromSeconds(300));
        await sanitizer;

        Assert.Equal("""{"formato":"ogg","duracaoMaxSegundos":300,"tamanhoMaxSaidaBytes":262144000,"timeoutSegundos":300}""" + "\n", json);
        Assert.Contains("job.json", ordem);
        Assert.Contains("pronto", ordem);
    }

    [Fact]
    public async Task Diretorio_do_job_e_o_GUID_da_midia_e_so_o_dono_acessa_em_Linux()
    {
        var id = Guid.NewGuid();
        await using var job = await Novo().CriarJobAsync(id);
        var dir = Path.Combine(_raiz, id.ToString("D"));
        Assert.True(Directory.Exists(dir));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
    }

    [Fact]
    public async Task Download_grava_o_in_bin_novo_e_ele_pode_ser_lido_de_volta()
    {
        var id = Guid.NewGuid();
        await using var job = await Novo().CriarJobAsync(id);
        await using (var w = job.AbrirOriginalParaEscrita()) await w.WriteAsync(new byte[] { 1, 2, 3 });
        await using var r = job.AbrirOriginalParaLeitura();
        var lido = new byte[3];
        await r.ReadExactlyAsync(lido);
        Assert.Equal(new byte[] { 1, 2, 3 }, lido);
        Assert.ThrowsAny<IOException>(() => job.AbrirOriginalParaEscrita()); // CreateNew: nunca reaproveita (nem segue symlink) um in.bin que já existe
    }

    [Fact]
    public async Task Resultado_ok_traz_a_saida_a_duracao_o_codec_e_os_streams()
    {
        var (r, _) = await Executar(d => Ok(d));

        Assert.True(r.Aprovado);
        Assert.Equal(Mp3, r.Saida);
        Assert.Equal(12.5m, r.DuracaoSegundos);
        Assert.Equal(("flac", 1), (r.Codec, r.StreamsAudio));
    }

    [Theory]
    [InlineData("codec_nao_permitido", MotivoDaSanitizacao.CodecNaoPermitido)]
    [InlineData("stream_nao_audio", MotivoDaSanitizacao.StreamNaoAudio)]
    [InlineData("streams_audio_invalido", MotivoDaSanitizacao.StreamsAudioInvalido)]
    [InlineData("formato_invalido", MotivoDaSanitizacao.FormatoInvalido)]
    [InlineData("falha_na_decodificacao", MotivoDaSanitizacao.FalhaNaDecodificacao)]
    [InlineData("saida_grande_demais", MotivoDaSanitizacao.SaidaGrandeDemais)]
    [InlineData("timeout", MotivoDaSanitizacao.Timeout)]
    [InlineData("job_invalido", MotivoDaSanitizacao.JobInvalido)]
    public async Task Recusa_com_motivo_da_lista_fechada_vira_o_enum(string texto, MotivoDaSanitizacao esperado)
    {
        var (r, _) = await Executar(d => File.WriteAllText(Path.Combine(d, "result.json"), $$"""{"ok":false,"motivo":"{{texto}}","streamsAudio":1,"codec":"adpcm_ms"}"""));
        Assert.False(r.Aprovado);
        Assert.Equal(esperado, r.Motivo);
        Assert.Null(r.Saida);
    }

    [Theory]
    [InlineData("""{"ok":false,"motivo":"texto livre do stderr: Invalid data found","streamsAudio":1,"codec":"x"}""")]
    [InlineData("""{"ok":false,"streamsAudio":1,"codec":"x"}""")]
    [InlineData("nao e json")]
    [InlineData("[]")]
    [InlineData("""{"ok":"sim","streamsAudio":1,"codec":"x"}""")]
    [InlineData("""{"ok":true,"streamsAudio":1,"codec":"x"}""")] // ok sem duração
    [InlineData("""{"ok":true,"duracaoSegundos":-1,"streamsAudio":1,"codec":"mp3"}""")]
    [InlineData("""{"ok":true,"duracaoSegundos":10,"streamsAudio":1,"codec":"mp3; rm -rf /"}""")]
    [InlineData("""{"ok":true,"duracaoSegundos":10,"streamsAudio":9999,"codec":"mp3"}""")]
    [InlineData("""{"a":{"b":{"c":{"d":{"e":1}}}}}""")]
    public async Task Resultado_malformado_ou_com_motivo_ou_codec_fora_do_formato_e_ResultadoInvalido(string result)
    {
        var (r, _) = await Executar(d => Ok(d, result: result));
        Assert.False(r.Aprovado);
        Assert.Equal(MotivoDaSanitizacao.ResultadoInvalido, r.Motivo);
    }

    [Fact]
    public async Task Result_json_acima_de_4_KB_nunca_e_lido()
    {
        var gigante = """{"ok":false,"motivo":"timeout","streamsAudio":1,"codec":"x"}""" + new string(' ', 5000);
        var (r, _) = await Executar(d => File.WriteAllText(Path.Combine(d, "result.json"), gigante));
        Assert.Equal(MotivoDaSanitizacao.ResultadoInvalido, r.Motivo);
    }

    [Fact]
    public async Task Out_mp3_acima_do_limite_de_saida_do_tipo_e_ResultadoInvalido()
    {
        var (r, _) = await Executar(d => Ok(d, outMp3: new string('a', 2000)), tamanhoMax: 1000);
        Assert.Equal(MotivoDaSanitizacao.ResultadoInvalido, r.Motivo);
    }

    [Fact]
    public async Task Ok_sem_out_mp3_e_ResultadoInvalido()
    {
        var (r, _) = await Executar(d => File.WriteAllText(Path.Combine(d, "result.json"), """{"ok":true,"duracaoSegundos":10,"streamsAudio":1,"codec":"mp3"}"""));
        Assert.Equal(MotivoDaSanitizacao.ResultadoInvalido, r.Motivo);
    }

    [Fact]
    public async Task Symlink_no_lugar_do_result_json_ou_do_out_mp3_nunca_e_seguido()
    {
        if (OperatingSystem.IsWindows()) return;
        var alvo = Path.Combine(_raiz, "segredo.txt");
        File.WriteAllText(alvo, """{"ok":true,"duracaoSegundos":10,"streamsAudio":1,"codec":"mp3"}""");

        var (r1, _) = await Executar(d => File.CreateSymbolicLink(Path.Combine(d, "result.json"), alvo));
        Assert.Equal(MotivoDaSanitizacao.ResultadoInvalido, r1.Motivo);

        var (r2, _) = await Executar(d =>
        {
            File.WriteAllText(Path.Combine(d, "outro-result"), """{"ok":true,"duracaoSegundos":10,"streamsAudio":1,"codec":"mp3"}""");
            File.CreateSymbolicLink(Path.Combine(d, "out.mp3"), alvo);
            File.Move(Path.Combine(d, "outro-result"), Path.Combine(d, "result.json"));
        });
        Assert.Equal(MotivoDaSanitizacao.ResultadoInvalido, r2.Motivo);
    }

    [Fact]
    public async Task Sem_result_json_dentro_do_prazo_e_Timeout()
    {
        var (r, _) = await Executar(_ => { }, timeout: TimeSpan.FromMilliseconds(200), folga: TimeSpan.FromMilliseconds(200));
        Assert.Equal(MotivoDaSanitizacao.Timeout, r.Motivo);
    }

    [Fact]
    public async Task Descartar_o_job_apaga_o_diretorio_inteiro()
    {
        var id = Guid.NewGuid();
        var job = await Novo().CriarJobAsync(id);
        await using (var w = job.AbrirOriginalParaEscrita()) await w.WriteAsync(new byte[] { 1 });
        await job.DisposeAsync();
        Assert.False(Directory.Exists(Path.Combine(_raiz, id.ToString("D"))));
    }

    [Fact]
    public async Task Novo_job_da_mesma_midia_comeca_de_um_diretorio_vazio()
    {
        var id = Guid.NewGuid();
        var dir = Path.Combine(_raiz, id.ToString("D"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "result.json"), "sobra de uma tentativa anterior");

        await using var job = await Novo().CriarJobAsync(id);

        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }
}

public class MarcadorDeDegradacaoTests
{
    [Fact]
    public void Degradado_escreve_o_marcador_com_o_motivo_e_Normal_o_apaga()
    {
        var arquivo = Path.Combine(Path.GetTempPath(), "wr-deg-" + Guid.NewGuid().ToString("N"));
        var m = new MarcadorDeDegradacao(arquivo, NullLogger<MarcadorDeDegradacao>.Instance);

        m.Degradado("clamd fora");
        Assert.Equal("clamd fora", File.ReadAllText(arquivo));
        m.Degradado("base velha");
        Assert.Equal("base velha", File.ReadAllText(arquivo));

        m.Normal();
        Assert.False(File.Exists(arquivo));
    }

    [Fact]
    public void Sem_arquivo_nao_quebra()
    {
        var m = new MarcadorDeDegradacao(null, NullLogger<MarcadorDeDegradacao>.Instance);
        m.Degradado("x");
        m.Normal();
    }
}
