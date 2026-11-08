using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Midias;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Midia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using WebRadio.Infrastructure.Trabalhos;
using Xunit;

namespace WebRadio.Application.Tests.Midias;

public sealed class SanitizerFalso : IAudioSanitizer
{
    public ResultadoSanitizacao? Resultado { get; set; }

    public Exception? ErroNoJob { get; set; }

    public int Execucoes { get; private set; }

    public int Jobs { get; private set; }

    public int JobsDescartados { get; private set; }

    public (FormatoDeAudio Formato, long Duracao, long Tamanho, TimeSpan Timeout)? Ultima { get; private set; }

    public Task<IJobDeSanitizacao> CriarJobAsync(Guid midiaId, CancellationToken ct = default)
    {
        Jobs++;
        return Task.FromResult<IJobDeSanitizacao>(new Job(this));
    }

    private sealed class Job(SanitizerFalso pai) : IJobDeSanitizacao
    {
        private readonly MemoryStream _original = new();

        public Stream AbrirOriginalParaEscrita() => new NaoFecha(_original);

        public Stream AbrirOriginalParaLeitura() => new MemoryStream(_original.ToArray());

        public Task<ResultadoSanitizacao> ExecutarAsync(FormatoDeAudio formato, long duracaoMaximaSegundos, long tamanhoMaximoDaSaidaBytes, TimeSpan timeoutDoSanitizer, CancellationToken ct = default)
        {
            pai.Execucoes++;
            pai.Ultima = (formato, duracaoMaximaSegundos, tamanhoMaximoDaSaidaBytes, timeoutDoSanitizer);
            return pai.ErroNoJob is null ? Task.FromResult(pai.Resultado!) : Task.FromException<ResultadoSanitizacao>(pai.ErroNoJob);
        }

        public ValueTask DisposeAsync()
        {
            pai.JobsDescartados++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NaoFecha(MemoryStream m) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => m.Length;
        public override long Position { get => m.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => m.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => m.WriteAsync(buffer, ct);
    }
}

public sealed class ScannerFalso : IMalwareScanner
{
    public ResultadoScan Resultado { get; set; } = ResultadoScan.Limpo;

    public Exception? Erro { get; set; }

    public int Chamadas { get; private set; }

    public Task<ResultadoScan> EscanearAsync(Stream conteudo, CancellationToken ct = default)
    {
        Chamadas++;
        return Erro is null ? Task.FromResult(Resultado) : Task.FromException<ResultadoScan>(Erro);
    }
}

public sealed class MarcadorFalso : IMarcadorDeDegradacao
{
    public string? Motivo { get; private set; }

    public bool Degradado_ => Motivo is not null;

    public void Degradado(string motivo) => Motivo = motivo;

    public void Normal() => Motivo = null;
}

/// <summary>E4-F05: o pipeline do Worker, etapa por etapa, com fakes do storage, do scanner e do sanitizer (o SQL e o MinIO reais ficam nos testes de Infrastructure).</summary>
public sealed class PipelineDeMidiaTests
{
    private static readonly byte[] Mp3 = [0xFF, 0xFB, 0x90, 0x00];

    private sealed class Cena
    {
        public MidiasFixture F { get; } = new();
        public SanitizerFalso Sanitizer { get; } = new();
        public ScannerFalso Scanner { get; } = new();
        public MarcadorFalso Marcador { get; } = new();
        public ListaDeLogs Logs { get; } = new();

        public PipelineDeMidia Pipeline => new(F.Db, new CapturaDeMidia(F.Db, F.Relogio), F.Storage, Scanner, Sanitizer, new OutboxEf(F.Db), F.Relogio, Marcador,
            Options.Create(F.Opcoes), Logs.Logger<PipelineDeMidia>());

        /// <summary>Uma mídia <c>Pendente</c> com o objeto na quarentena (conteúdo, tamanho e ETag batendo com o registrado no concluir).</summary>
        public ArquivoMidia Pendente(byte[]? conteudo = null, TipoMidia tipo = TipoMidia.Vinheta, string? titulo = null, string? etagNoStorage = null)
        {
            conteudo ??= [.. Mp3, .. new byte[500]];
            var m = new ArquivoMidia(Guid.CreateVersion7(), "a.mp3", titulo, null, tipo, "audio/mpeg", conteudo.Length, F.Usuario, F.Relogio.UtcNow);
            m.ConcluirUpload(conteudo.Length, "etag-ok", F.Relogio.UtcNow);
            F.Db.ArquivosMidia.Add(m);
            F.Db.SaveChanges();
            F.Storage.Objetos[$"quarentena/{m.ChaveStorage}"] = new InfoObjeto(conteudo.Length, "audio/mpeg", etagNoStorage ?? "\"etag-ok\"");
            F.Storage.Conteudos[$"quarentena/{m.ChaveStorage}"] = conteudo;
            return m;
        }

        public static byte[] SaidaMp3(decimal segundos) => [.. Mp3, .. new byte[(int)(segundos * 24_000m) - Mp3.Length]];

        public void SanitizerAprova(decimal segundos = 10m, byte[]? saida = null, string codec = "mp3") =>
            Sanitizer.Resultado = new ResultadoSanitizacao(true, null, saida ?? SaidaMp3(segundos), segundos, 1, codec);

        public ArquivoMidia Recarregar(Guid id) => F.Db.ArquivosMidia.AsNoTracking().Single(m => m.Id == id);

        public int Eventos(string tipo) => F.Db.EventosOutbox.Count(e => e.Tipo == tipo);
    }

    // ---- o caminho feliz ----

    [Fact]
    public async Task Fila_vazia_nao_faz_nada()
    {
        var c = new Cena();
        Assert.Equal(PassoDoPipeline.FilaVazia, await c.Pipeline.ProcessarProximaAsync(default));
        Assert.Equal(0, c.Scanner.Chamadas);
    }

    [Fact]
    public async Task Caminho_feliz_aprova_so_a_saida_em_midia_apaga_o_original_e_avisa_pelo_outbox()
    {
        var c = new Cena();
        var m = c.Pendente();
        var chaveDoOriginal = m.ChaveStorage;
        c.SanitizerAprova(10m);

        Assert.NotEqual(PassoDoPipeline.FilaVazia, await c.Pipeline.ProcessarProximaAsync(default));

        var r = c.Recarregar(m.Id);
        Assert.Equal(StatusSanitizacao.Aprovado, r.StatusSanitizacao);
        Assert.Equal("midia", r.Bucket);
        Assert.Equal(chaveDoOriginal + ".mp3", r.ChaveStorage);
        Assert.Equal("audio/mpeg", r.MimeType);
        Assert.Equal(10m, r.DuracaoSegundos);
        Assert.Equal(240_000, r.TamanhoBytes);
        Assert.Equal(32, r.HashOriginalSHA256!.Length);
        var put = Assert.Single(c.F.Storage.Puts);
        Assert.Equal(("midia", chaveDoOriginal + ".mp3", "audio/mpeg"), (put.Bucket, put.Chave, put.ContentType));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(put.Bytes), r.HashSHA256);
        Assert.Contains($"quarentena/{chaveDoOriginal}", c.F.Storage.Apagados);
        Assert.Equal(1, c.Eventos("MidiaSanitizada"));
        Assert.Equal(1, c.Scanner.Chamadas);
        Assert.Equal(1, c.Sanitizer.JobsDescartados);
    }

    [Fact]
    public async Task O_sanitizer_recebe_o_formato_dos_magic_bytes_e_os_limites_e_o_timeout_do_tipo()
    {
        var c = new Cena();
        c.Pendente(conteudo: [.. "OggS"u8.ToArray(), .. new byte[200]], tipo: TipoMidia.Musica, titulo: "Aurora");
        c.SanitizerAprova(200m);

        await c.Pipeline.ProcessarProximaAsync(default);

        var u = c.Sanitizer.Ultima!.Value;
        Assert.Equal(FormatoDeAudio.Ogg, u.Formato);
        Assert.Equal(20 * 60, u.Duracao);
        Assert.Equal(250L * 1024 * 1024, u.Tamanho);
        Assert.Equal(TimeSpan.FromSeconds(300), u.Timeout); // E4-D15: ¼ de 20 min
    }

    [Theory]
    [InlineData(TipoMidia.Vinheta, 60)]
    [InlineData(TipoMidia.Comercial, 60)]
    [InlineData(TipoMidia.Musica, 300)]
    public void Timeout_do_sanitizer_e_o_maior_entre_60_s_e_um_quarto_da_duracao_maxima(TipoMidia tipo, int segundos)
        => Assert.Equal(TimeSpan.FromSeconds(segundos), new MidiaOptions().TimeoutDoSanitizer(tipo));

    [Fact]
    public void O_lease_cobre_download_scan_sanitizer_e_upload_mais_60_s()
    {
        var o = new MidiaOptions();
        // Música: 250 MiB a 10 MiB/s = 25 s (download) + 120 s (scan) + 300 s (sanitizer) + 25 s (upload) + 60 s
        Assert.Equal(TimeSpan.FromSeconds(25 + 120 + 300 + 25 + 60), o.LeaseDoPipeline(TipoMidia.Musica, 120));
        Assert.True(o.LeaseDoPipeline(TipoMidia.Vinheta, 120) > o.TimeoutDoSanitizer(TipoMidia.Vinheta) + TimeSpan.FromSeconds(120));
    }

    // ---- stat, download condicional e magic bytes (nada disso chega ao antivírus nem ao ffmpeg) ----

    [Fact]
    public async Task ETag_do_stat_diferente_do_registrado_e_TOCTOU_rejeita_e_apaga_sem_chamar_scanner_nem_sanitizer()
    {
        var c = new Cena();
        var m = c.Pendente(etagNoStorage: "\"etag-trocado\"");

        await c.Pipeline.ProcessarProximaAsync(default);

        var r = c.Recarregar(m.Id);
        Assert.Equal(StatusSanitizacao.Rejeitado, r.StatusSanitizacao);
        Assert.Equal(PipelineDeMidia.MotivoArquivoAlterado, r.MotivoRejeicao);
        Assert.Contains($"quarentena/{m.ChaveStorage}", c.F.Storage.Apagados);
        Assert.Equal(0, c.Scanner.Chamadas);
        Assert.Equal(0, c.Sanitizer.Jobs);
        Assert.Empty(c.F.Storage.Puts);
    }

    [Fact]
    public async Task Tamanho_do_stat_diferente_do_registrado_tambem_e_TOCTOU()
    {
        var c = new Cena();
        var m = c.Pendente();
        c.F.Storage.Objetos[$"quarentena/{m.ChaveStorage}"] = new InfoObjeto(9999, "audio/mpeg", "\"etag-ok\"");
        await c.Pipeline.ProcessarProximaAsync(default);
        Assert.Equal(PipelineDeMidia.MotivoArquivoAlterado, c.Recarregar(m.Id).MotivoRejeicao);
    }

    [Fact]
    public async Task GET_condicional_que_responde_412_entre_o_stat_e_o_download_rejeita_e_nunca_envia_nada_a_midia()
    {
        var c = new Cena();
        var m = c.Pendente();
        c.F.Storage.ErroNoBaixar = new EtagDivergenteException("412");

        await c.Pipeline.ProcessarProximaAsync(default);

        Assert.Equal(PipelineDeMidia.MotivoArquivoAlterado, c.Recarregar(m.Id).MotivoRejeicao);
        Assert.Empty(c.F.Storage.Puts);
        Assert.Equal(0, c.Scanner.Chamadas);
    }

    [Fact]
    public async Task Objeto_que_passa_do_limite_do_tipo_no_download_e_rejeitado()
    {
        var c = new Cena();
        var m = c.Pendente(conteudo: new byte[10 * 1024 * 1024 + 1]);
        await c.Pipeline.ProcessarProximaAsync(default);
        var r = c.Recarregar(m.Id);
        Assert.Equal(StatusSanitizacao.Rejeitado, r.StatusSanitizacao);
        Assert.Equal(0, c.Scanner.Chamadas);
    }

    [Fact]
    public async Task Objeto_ausente_da_quarentena_e_rejeitado()
    {
        var c = new Cena();
        var m = c.Pendente();
        c.F.Storage.Objetos.Clear();
        await c.Pipeline.ProcessarProximaAsync(default);
        Assert.Equal(PipelineDeMidia.MotivoArquivoAusente, c.Recarregar(m.Id).MotivoRejeicao);
    }

    [Theory]
    [InlineData("#EXTM3U\nhttp://api:8080/health/live\n")]
    [InlineData("ffconcat version 1.0\nfile /etc/passwd\n")]
    [InlineData("<html>oi</html>")]
    public async Task Formato_nao_reconhecido_pelos_magic_bytes_e_rejeitado_SEM_antivirus_e_SEM_ffprobe(string conteudo)
    {
        var c = new Cena();
        var m = c.Pendente(conteudo: System.Text.Encoding.ASCII.GetBytes(conteudo));

        await c.Pipeline.ProcessarProximaAsync(default);

        var r = c.Recarregar(m.Id);
        Assert.Equal(StatusSanitizacao.Rejeitado, r.StatusSanitizacao);
        Assert.Equal(PipelineDeMidia.MotivoFormatoNaoReconhecido, r.MotivoRejeicao);
        Assert.Equal(0, c.Scanner.Chamadas);
        Assert.Equal(0, c.Sanitizer.Execucoes);
        Assert.NotNull(r.HashOriginalSHA256); // o hash do original fica para a inteligência de malware
        Assert.Contains($"quarentena/{m.ChaveStorage}", c.F.Storage.Apagados);
    }

    // ---- antivírus (E4-D13) ----

    [Fact]
    public async Task Infectado_vai_para_Quarentena_o_objeto_FICA_e_o_sanitizer_nunca_ve_o_arquivo()
    {
        var c = new Cena();
        var m = c.Pendente();
        c.Scanner.Resultado = ResultadoScan.Infectado;

        await c.Pipeline.ProcessarProximaAsync(default);

        var r = c.Recarregar(m.Id);
        Assert.Equal(StatusSanitizacao.Quarentena, r.StatusSanitizacao);
        Assert.Empty(c.F.Storage.Apagados);
        Assert.Equal(0, c.Sanitizer.Execucoes);
        Assert.Equal(1, c.Eventos("MidiaSanitizada"));
    }

    [Fact]
    public async Task Antivirus_indisponivel_quatro_ciclos_seguidos_deixa_Pendente_com_tentativa_zero_e_nunca_aprova_sem_scan()
    {
        var c = new Cena();
        var m = c.Pendente();
        c.Scanner.Erro = new ScannerIndisponivelException("clamd fora");

        for (var i = 0; i < 4; i++) Assert.Equal(PassoDoPipeline.InfraestruturaFora, await c.Pipeline.ProcessarProximaAsync(default));

        var r = c.Recarregar(m.Id);
        Assert.Equal(StatusSanitizacao.Pendente, r.StatusSanitizacao);
        Assert.Equal(0, r.TentativasSanitizacao);
        Assert.Equal("clamd fora", c.Marcador.Motivo);
        Assert.Equal(0, c.Sanitizer.Execucoes);
        Assert.Empty(c.F.Storage.Puts);

        // Quando o clamd volta, o MESMO arquivo é processado.
        c.Scanner.Erro = null;
        c.SanitizerAprova();
        Assert.NotEqual(PassoDoPipeline.FilaVazia, await c.Pipeline.ProcessarProximaAsync(default));
        Assert.Equal(StatusSanitizacao.Aprovado, c.Recarregar(m.Id).StatusSanitizacao);
        Assert.Null(c.Marcador.Motivo); // saiu de Degraded
    }

    [Fact]
    public async Task Falha_no_scan_conta_a_tentativa_e_a_terceira_rejeita_por_falha_de_processamento_e_apaga()
    {
        var c = new Cena();
        var m = c.Pendente();
        c.Scanner.Erro = new FalhaNoScanException("clamd caiu no meio");

        await c.Pipeline.ProcessarProximaAsync(default);
        Assert.Equal((StatusSanitizacao.Pendente, (byte)1), (c.Recarregar(m.Id).StatusSanitizacao, c.Recarregar(m.Id).TentativasSanitizacao));
        await c.Pipeline.ProcessarProximaAsync(default);
        Assert.Equal((byte)2, c.Recarregar(m.Id).TentativasSanitizacao);
        await c.Pipeline.ProcessarProximaAsync(default);

        var r = c.Recarregar(m.Id);
        Assert.Equal(StatusSanitizacao.Rejeitado, r.StatusSanitizacao);
        Assert.Equal("falha de processamento", r.MotivoRejeicao);
        Assert.Contains($"quarentena/{m.ChaveStorage}", c.F.Storage.Apagados);
        Assert.Equal(1, c.Eventos("MidiaSanitizada"));
    }

    [Fact]
    public async Task Scanner_NoOp_aprova_com_SCAN_IGNORADO_e_a_auditoria_MIDIA_APROVADA_SEM_SCAN()
    {
        var c = new Cena();
        var m = c.Pendente();
        c.Scanner.Resultado = ResultadoScan.Ignorado;
        c.SanitizerAprova();

        await c.Pipeline.ProcessarProximaAsync(default);

        Assert.Equal(StatusSanitizacao.Aprovado, c.Recarregar(m.Id).StatusSanitizacao);
        Assert.Contains(c.Logs.Linhas, l => l.Contains("SCAN_IGNORADO"));
        Assert.Contains(c.Logs.Linhas, l => l.Contains("MIDIA_APROVADA_SEM_SCAN") && l.Contains(m.Id.ToString()));
    }

    // ---- o resultado do sanitizer ----

    [Theory]
    [InlineData(MotivoDaSanitizacao.CodecNaoPermitido, "codec de áudio não permitido")]
    [InlineData(MotivoDaSanitizacao.StreamNaoAudio, "o arquivo tem um stream que não é áudio")]
    [InlineData(MotivoDaSanitizacao.StreamsAudioInvalido, "o arquivo precisa ter exatamente um stream de áudio")]
    [InlineData(MotivoDaSanitizacao.FormatoInvalido, "o arquivo não é um áudio MP3, OGG, WAV ou FLAC válido")]
    [InlineData(MotivoDaSanitizacao.FalhaNaDecodificacao, "o áudio não pôde ser decodificado")]
    [InlineData(MotivoDaSanitizacao.SaidaGrandeDemais, "o áudio é grande demais para o tipo de mídia")]
    public async Task Rejeicao_do_sanitizer_vira_a_mensagem_FIXA_do_codigo_e_nada_chega_a_midia(MotivoDaSanitizacao motivo, string mensagem)
    {
        var c = new Cena();
        var m = c.Pendente();
        c.Sanitizer.Resultado = new ResultadoSanitizacao(false, motivo, null, null, 1, "adpcm_ms");

        await c.Pipeline.ProcessarProximaAsync(default);

        var r = c.Recarregar(m.Id);
        Assert.Equal((StatusSanitizacao.Rejeitado, mensagem), (r.StatusSanitizacao, r.MotivoRejeicao));
        Assert.Empty(c.F.Storage.Puts);
        Assert.Contains($"quarentena/{m.ChaveStorage}", c.F.Storage.Apagados);
        Assert.Equal(1, c.Eventos("MidiaSanitizada"));
    }

    [Theory]
    [InlineData(MotivoDaSanitizacao.Timeout)]
    [InlineData(MotivoDaSanitizacao.ResultadoInvalido)]
    [InlineData(MotivoDaSanitizacao.JobInvalido)]
    public async Task Timeout_ou_resultado_invalido_do_sanitizer_conta_tentativa_e_a_terceira_rejeita_DoD_6(MotivoDaSanitizacao motivo)
    {
        var c = new Cena();
        var m = c.Pendente();
        c.Sanitizer.Resultado = new ResultadoSanitizacao(false, motivo, null, null, 0, null);

        for (var i = 0; i < 2; i++)
        {
            await c.Pipeline.ProcessarProximaAsync(default);
            Assert.Equal(StatusSanitizacao.Pendente, c.Recarregar(m.Id).StatusSanitizacao);
        }

        await c.Pipeline.ProcessarProximaAsync(default);
        var r = c.Recarregar(m.Id);
        Assert.Equal(StatusSanitizacao.Rejeitado, r.StatusSanitizacao);
        Assert.Equal("falha de processamento", r.MotivoRejeicao);
        Assert.Empty(c.F.Storage.Puts);
    }

    [Fact]
    public async Task Saida_que_nao_e_mp3_ou_incoerente_conta_como_falha_e_nunca_vai_a_midia()
    {
        var c = new Cena();
        var m = c.Pendente();

        // cabeçalho de outra coisa
        c.SanitizerAprova(10m, saida: [.. "#EXTM3U"u8.ToArray(), .. new byte[239_993]]);
        await c.Pipeline.ProcessarProximaAsync(default);
        Assert.Equal((byte)1, c.Recarregar(m.Id).TentativasSanitizacao);

        // duração declarada incoerente com os bytes lidos (declara 10 s e entrega 3 s de bytes)
        c.SanitizerAprova(10m, saida: Cena.SaidaMp3(3m));
        await c.Pipeline.ProcessarProximaAsync(default);
        Assert.Equal((byte)2, c.Recarregar(m.Id).TentativasSanitizacao);

        // saída que bateu no limite de tamanho (o -fs cortou) nunca vale
        c.Sanitizer.Resultado = new ResultadoSanitizacao(true, null, new byte[10 * 1024 * 1024], 400m, 1, "mp3");
        await c.Pipeline.ProcessarProximaAsync(default);
        Assert.Equal(StatusSanitizacao.Rejeitado, c.Recarregar(m.Id).StatusSanitizacao);
        Assert.Empty(c.F.Storage.Puts);
    }

    [Theory]
    [InlineData(0.5, 1, 60)] // vinheta: mínimo 1 s
    [InlineData(61, 1, 60)] // vinheta: máximo 60 s
    public async Task Duracao_da_SAIDA_fora_do_limite_do_tipo_rejeita_e_nada_vai_a_midia(double segundos, int minimo, int maximo)
    {
        var c = new Cena();
        var m = c.Pendente();
        c.SanitizerAprova((decimal)segundos);

        await c.Pipeline.ProcessarProximaAsync(default);

        var r = c.Recarregar(m.Id);
        Assert.Equal(StatusSanitizacao.Rejeitado, r.StatusSanitizacao);
        Assert.Contains($"{minimo}s a {maximo}s", r.MotivoRejeicao);
        Assert.Empty(c.F.Storage.Puts);
    }

    [Fact]
    public async Task Saida_ja_aprovada_com_o_mesmo_hash_e_duplicada_e_nada_vai_a_midia()
    {
        var c = new Cena();
        var primeira = c.F.SemearAprovada(tipo: TipoMidia.Vinheta, titulo: null);
        var igual = Cena.SaidaMp3(10m);
        // força o hash da aprovada existente a ser o da saída que o sanitizer vai devolver
        var m = c.Pendente();
        c.SanitizerAprova(10m, saida: igual);
        typeof(ArquivoMidia).GetProperty(nameof(ArquivoMidia.HashSHA256))!.SetValue(primeira, System.Security.Cryptography.SHA256.HashData(igual));
        c.F.Db.SaveChanges();

        await c.Pipeline.ProcessarProximaAsync(default);

        var r = c.Recarregar(m.Id);
        Assert.Equal(StatusSanitizacao.Rejeitado, r.StatusSanitizacao);
        Assert.Equal(PipelineDeMidia.MotivoDuplicado, r.MotivoRejeicao);
        Assert.Empty(c.F.Storage.Puts);
    }

    // ---- infraestrutura ----

    [Fact]
    public async Task Falha_do_MinIO_no_upload_da_saida_e_infraestrutura_volta_a_Pendente_com_a_tentativa_devolvida_e_sem_evento()
    {
        var c = new Cena();
        var m = c.Pendente();
        c.SanitizerAprova();
        c.F.Storage.ErroNoPut = new InvalidOperationException("minio fora");

        await c.Pipeline.ProcessarProximaAsync(default);

        var r = c.Recarregar(m.Id);
        Assert.Equal((StatusSanitizacao.Pendente, (byte)0), (r.StatusSanitizacao, r.TentativasSanitizacao));
        Assert.Equal(0, c.Eventos("MidiaSanitizada"));
        Assert.DoesNotContain($"quarentena/{m.ChaveStorage}", c.F.Storage.Apagados); // o original só sai depois de a saída entrar
    }

    [Fact]
    public async Task Cancelamento_do_shutdown_deixa_o_lease_e_nao_devolve_nem_rejeita()
    {
        var c = new Cena();
        var m = c.Pendente();
        c.Scanner.Erro = new OperationCanceledException();
        using var cancelar = new CancellationTokenSource();
        await cancelar.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.Pipeline.ProcessarProximaAsync(cancelar.Token));
        Assert.Equal(StatusSanitizacao.Pendente, c.Recarregar(m.Id).StatusSanitizacao); // nem chegou a capturar
    }

    // ---- lease ----

    [Fact]
    public async Task Lease_vencido_com_o_Worker_morto_volta_para_Pendente_no_ciclo_seguinte_S_M04()
    {
        var c = new Cena();
        var m = c.Pendente();
        // um Worker que morreu: o item ficou EmAnalise com o lease de agora
        var viva = c.F.Db.ArquivosMidia.Single();
        viva.IniciarAnalise(c.F.Relogio.UtcNow);
        c.F.Db.SaveChanges();
        c.F.Relogio.UtcNow = c.F.Relogio.UtcNow.Add(c.F.Opcoes.LeaseDoPipeline(TipoMidia.Vinheta, 120)).AddSeconds(1);
        c.SanitizerAprova();

        await c.Pipeline.ProcessarProximaAsync(default); // libera o lease (conta como falha) e, no mesmo ciclo, captura de novo

        var r = c.Recarregar(m.Id);
        Assert.Equal(StatusSanitizacao.Aprovado, r.StatusSanitizacao);
    }

    [Fact]
    public async Task Lease_vencido_pela_terceira_vez_rejeita_e_avisa()
    {
        var c = new Cena();
        var m = c.Pendente();
        for (var i = 0; i < 3; i++)
        {
            var viva = c.F.Db.ArquivosMidia.Single();
            viva.IniciarAnalise(c.F.Relogio.UtcNow);
            c.F.Db.SaveChanges();
            c.F.Relogio.UtcNow = c.F.Relogio.UtcNow.Add(c.F.Opcoes.LeaseDoPipeline(TipoMidia.Vinheta, 120)).AddSeconds(1);
            if (i < 2)
            {
                c.F.Db.ArquivosMidia.Single().LiberarLeaseExpirado(c.F.Relogio.UtcNow, c.F.Opcoes.LeaseDoPipeline(TipoMidia.Vinheta, 120));
                c.F.Db.SaveChanges();
            }
        }

        Assert.Equal(PassoDoPipeline.FilaVazia, await c.Pipeline.ProcessarProximaAsync(default));

        var r = c.Recarregar(m.Id);
        Assert.Equal((StatusSanitizacao.Rejeitado, "falha de processamento"), (r.StatusSanitizacao, r.MotivoRejeicao));
        Assert.Equal(1, c.Eventos("MidiaSanitizada"));
    }

    [Fact]
    public async Task Lease_ainda_valido_nao_e_tocado()
    {
        var c = new Cena();
        var m = c.Pendente();
        c.F.Db.ArquivosMidia.Single().IniciarAnalise(c.F.Relogio.UtcNow);
        c.F.Db.SaveChanges();
        c.F.Relogio.UtcNow = c.F.Relogio.UtcNow.AddSeconds(30);

        Assert.Equal(PassoDoPipeline.FilaVazia, await c.Pipeline.ProcessarProximaAsync(default));
        Assert.Equal(StatusSanitizacao.EmAnalise, c.Recarregar(m.Id).StatusSanitizacao);
    }

    [Fact]
    public async Task Captura_pega_a_mais_antiga_primeiro()
    {
        var c = new Cena();
        var velha = c.Pendente();
        c.F.Relogio.UtcNow = c.F.Relogio.UtcNow.AddMinutes(1);
        var nova = c.Pendente();
        c.SanitizerAprova();

        await c.Pipeline.ProcessarProximaAsync(default);

        Assert.Equal(StatusSanitizacao.Aprovado, c.Recarregar(velha.Id).StatusSanitizacao);
        Assert.Equal(StatusSanitizacao.Pendente, c.Recarregar(nova.Id).StatusSanitizacao);
    }
}
