using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Midia;

namespace WebRadio.Application.Features.Midias;

/// <summary>
/// Pipeline de sanitização do Worker (<c>00</c> Fluxo C passo 3, 08 E4-F05). Um item por vez (E4-D1): captura → <c>stat</c> → download condicional direto no tmpfs do job → hash do
/// original → magic bytes → ClamAV no ORIGINAL → sanitizer → conferência da saída → só então o <c>PutAsync</c> em <c>midia/</c> → <c>Aprovado</c> + <c>MidiaSanitizada</c> no outbox
/// (mesma transação) → apaga o original. Nada chega a <c>midia/</c> antes de o resultado estar aprovado.
/// <para>
/// Falha de INFRAESTRUTURA (antivírus fora ou com base velha, MinIO, SQL, disco do job) devolve o item a <c>Pendente</c> COM a tentativa (E4-D13); falha do ARQUIVO (sanitizer morto
/// no timeout, <c>clamd</c> que cai no meio do scan, <c>result.json</c> inválido) conta, e a 3ª vira <c>Rejeitado</c> ("falha de processamento"). Cancelamento (shutdown) deixa o lease:
/// ele vence e o item volta pelo próprio relógio.
/// </para>
/// </summary>
public sealed class PipelineDeMidia(
    IRadioDbContext db,
    ICapturaDeMidia captura,
    IObjectStorage storage,
    IMalwareScanner scanner,
    IAudioSanitizer sanitizer,
    IOutbox outbox,
    IClock clock,
    IMarcadorDeDegradacao degradacao,
    IOptions<MidiaOptions> opcoes,
    ILogger<PipelineDeMidia> logger)
{
    /// <summary>Segundos do orçamento do scan (<c>Seguranca:TimeoutScanSegundos</c>), só para calcular o lease.</summary>
    public int TimeoutDoScanSegundos { get; init; } = 120;

    /// <summary>Mensagens FIXAS por motivo (E4-F05): nada do stderr do ffmpeg nem do <c>result.json</c> chega ao registro.</summary>
    public static string MensagemDo(MotivoDaSanitizacao motivo) => motivo switch
    {
        MotivoDaSanitizacao.FormatoInvalido => "o arquivo não é um áudio MP3, OGG, WAV ou FLAC válido",
        MotivoDaSanitizacao.StreamsAudioInvalido => "o arquivo precisa ter exatamente um stream de áudio",
        MotivoDaSanitizacao.StreamNaoAudio => "o arquivo tem um stream que não é áudio",
        MotivoDaSanitizacao.CodecNaoPermitido => "codec de áudio não permitido",
        MotivoDaSanitizacao.FalhaNaDecodificacao => "o áudio não pôde ser decodificado",
        MotivoDaSanitizacao.SaidaGrandeDemais => "o áudio é grande demais para o tipo de mídia",
        _ => "falha de processamento",
    };

    public const string MotivoFormatoNaoReconhecido = "o arquivo não é um áudio MP3, OGG, WAV ou FLAC (formato não reconhecido)";
    public const string MotivoArquivoAlterado = "o arquivo foi alterado depois do envio";
    public const string MotivoArquivoAusente = "o arquivo não está mais na quarentena";
    public const string MotivoDuplicado = "este áudio já existe na biblioteca";
    public const string MotivoInfectado = "o antivírus detectou ameaça no arquivo";
    public const string MotivoSaidaInvalida = "a saída da sanitização não passou na conferência";

    /// <summary>Um item: devolve <c>false</c> se a fila estava vazia.</summary>
    public async Task<bool> ProcessarProximaAsync(CancellationToken ct)
    {
        await LiberarLeasesVencidosAsync(ct);
        if (await captura.CapturarProximaAsync(ct) is not { } id) return false;

        var midia = await db.ArquivosMidia.FirstAsync(m => m.Id == id, ct);
        try
        {
            await ProcessarAsync(midia, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown: o lease vence e o item volta
        }
        catch (ScannerIndisponivelException e)
        {
            degradacao.Degradado(e.Motivo);
            logger.LogError("PIPELINE_MIDIA_ANTIVIRUS_INDISPONIVEL midia {MidiaId}: {Motivo}. O arquivo continua Pendente e a tentativa foi devolvida.", midia.Id, e.Motivo);
            await DevolverAsync(midia, ct);
        }
        catch (FalhaNoScanException e)
        {
            logger.LogWarning("PIPELINE_MIDIA_FALHA_NO_SCAN midia {MidiaId}: {Motivo}", midia.Id, e.Motivo);
            await FalhaDoArquivoAsync(midia, ct);
        }
        catch (Exception e)
        {
            // Qualquer outra (MinIO, SQL, disco do job, rede) é infraestrutura: o arquivo não tem culpa.
            logger.LogError(e, "PIPELINE_MIDIA_INFRAESTRUTURA midia {MidiaId} ({Tipo}): o item volta a Pendente e a tentativa é devolvida.", midia.Id, e.GetType().Name);
            await DevolverAsync(midia, ct);
        }

        return true;
    }

    /// <summary>Lease vencido (Worker morto no meio) conta como falha de processamento e volta à fila; na 3ª o item é rejeitado (S-M04).</summary>
    private async Task LiberarLeasesVencidosAsync(CancellationToken ct)
    {
        var o = opcoes.Value;
        var agora = clock.UtcNow;
        var emAnalise = await db.ArquivosMidia.Where(m => m.StatusSanitizacao == StatusSanitizacao.EmAnalise).ToListAsync(ct);
        var mudou = false;
        foreach (var m in emAnalise.Where(m => m.EmAnaliseDesdeUtc is { } desde && agora - desde >= o.LeaseDoPipeline(m.TipoMidia, TimeoutDoScanSegundos)))
        {
            m.LiberarLeaseExpirado(agora, o.LeaseDoPipeline(m.TipoMidia, TimeoutDoScanSegundos));
            logger.LogWarning("PIPELINE_MIDIA_LEASE_VENCIDO midia {MidiaId} tentativas {Tentativas}", m.Id, m.TentativasSanitizacao);
            if (m.StatusSanitizacao == StatusSanitizacao.Rejeitado) Notificar(m);
            mudou = true;
        }

        if (mudou) await db.SaveChangesAsync(ct);
    }

    private async Task ProcessarAsync(ArquivoMidia midia, CancellationToken ct)
    {
        var o = opcoes.Value;
        var limite = o.Limite(midia.TipoMidia);

        // 1) stat: tamanho e ETag registrados no concluir. Divergência = o objeto foi trocado depois (a POST policy ainda aceita POST na mesma chave): TOCTOU (S-M02).
        var info = await storage.HeadAsync(Buckets.Quarentena, midia.ChaveStorage, ct);
        if (info is null)
        {
            await RejeitarAsync(midia, MotivoArquivoAusente, apagarObjeto: false, ct);
            return;
        }

        if (info.TamanhoBytes != midia.TamanhoBytes || !string.Equals(info.ETag?.Trim('"'), midia.EtagUpload, StringComparison.Ordinal))
        {
            await RejeitarAsync(midia, MotivoArquivoAlterado, apagarObjeto: true, ct);
            return;
        }

        await using var job = await sanitizer.CriarJobAsync(midia.Id, ct);

        // 2) download condicional DIRETO no tmpfs do job (nunca no /tmp do Worker, que dobraria a memória): If-Match = o ETag registrado, corte em limite + 1.
        try
        {
            await using var destino = job.AbrirOriginalParaEscrita();
            await storage.BaixarAsync(Buckets.Quarentena, midia.ChaveStorage, midia.EtagUpload!, limite.TamanhoMaximoBytes, destino, ct);
        }
        catch (EtagDivergenteException)
        {
            await RejeitarAsync(midia, MotivoArquivoAlterado, apagarObjeto: true, ct);
            return;
        }
        catch (ObjetoGrandeDemaisException)
        {
            await RejeitarAsync(midia, MensagemDo(MotivoDaSanitizacao.SaidaGrandeDemais), apagarObjeto: true, ct);
            return;
        }

        // 3) hash do original (auditoria, não único) e magic bytes; só eles decidem o demuxer. Formato desconhecido: Rejeitado SEM chamar antivírus nem ffprobe.
        byte[] inicio = new byte[DetectorDeFormato.BytesNecessarios];
        int lidosDoInicio;
        await using (var leitura = job.AbrirOriginalParaLeitura())
        {
            // O hash do original é de escrita única; numa nova tentativa do MESMO arquivo ele já está gravado (e o ETag garante que o conteúdo é o mesmo).
            var hashOriginal = await SHA256.HashDataAsync(leitura, ct);
            if (midia.HashOriginalSHA256 is null) midia.RegistrarHashOriginal(hashOriginal);
            leitura.Position = 0;
            lidosDoInicio = await leitura.ReadAtLeastAsync(inicio, inicio.Length, throwOnEndOfStream: false, ct);
        }

        if (DetectorDeFormato.Detectar(inicio.AsSpan(0, lidosDoInicio)) is not { } formato)
        {
            await RejeitarAsync(midia, MotivoFormatoNaoReconhecido, apagarObjeto: true, ct);
            return;
        }

        // 4) ClamAV no ORIGINAL, antes do sanitizer (S-A02). Indisponível/ERROR sobem como exceção e são tratadas fora (infra × arquivo, E4-D13).
        ResultadoScan scan;
        await using (var leitura = job.AbrirOriginalParaLeitura()) scan = await scanner.EscanearAsync(leitura, ct);
        degradacao.Normal();
        switch (scan)
        {
            case ResultadoScan.Infectado:
                // O objeto FICA na quarentena por 30 dias para análise (S-B06); só a Manutenção o apaga.
                midia.MarcarQuarentena(MotivoInfectado, clock.UtcNow);
                Notificar(midia);
                await db.SaveChangesAsync(ct);
                logger.LogWarning("PIPELINE_MIDIA_QUARENTENA midia {MidiaId}", midia.Id);
                return;
            case ResultadoScan.Ignorado:
                // Só o NoOp (Development com PermitirScannerNoOp) devolve isto, e nunca sai de lá (E4-D9).
                logger.LogWarning("SCAN_IGNORADO midia {MidiaId}: aprovada SEM antivírus (scanner NoOp, só Development)", midia.Id);
                logger.LogWarning("MIDIA_APROVADA_SEM_SCAN midia {MidiaId} hashOriginalSha256 {Hash}", midia.Id, Convert.ToHexStringLower(midia.HashOriginalSHA256!));
                break;
        }

        // 5) sanitizer: o diretório do job, job.json e `pronto`; o resultado volta lido UMA vez, sem symlink e com teto.
        var timeout = o.TimeoutDoSanitizer(midia.TipoMidia);
        var tamanhoMaxSaida = limite.TamanhoMaximoBytes;
        var resultado = await job.ExecutarAsync(formato, (long)limite.DuracaoMaxima.TotalSeconds, tamanhoMaxSaida, timeout, ct);
        if (!resultado.Aprovado)
        {
            var motivo = resultado.Motivo ?? MotivoDaSanitizacao.ResultadoInvalido;
            logger.LogInformation("PIPELINE_MIDIA_RECUSADA midia {MidiaId} formato {Formato} codec {Codec} motivo {Motivo}", midia.Id, formato, resultado.Codec ?? "-", motivo);
            if (motivo is MotivoDaSanitizacao.Timeout or MotivoDaSanitizacao.ResultadoInvalido or MotivoDaSanitizacao.JobInvalido)
                await FalhaDoArquivoAsync(midia, ct); // processamento: conta a tentativa (E4-D13)
            else
                await RejeitarAsync(midia, MensagemDo(motivo), apagarObjeto: true, ct);
            return;
        }

        // 6) conferência da saída (S-B09): só o que o Worker leu e mediu vale; o que o sanitizer afirmou no result.json é dado, não prova.
        var saida = resultado.Saida;
        var duracao = resultado.Duracao();
        if (saida is null || duracao is null || saida.Length == 0 || saida.Length >= tamanhoMaxSaida
            || !DetectorDeFormato.CabecalhoDeMp3(saida) || !DetectorDeFormato.DuracaoCoerenteComOsBytes(saida.Length, duracao.Value))
        {
            logger.LogWarning("PIPELINE_MIDIA_SAIDA_INVALIDA midia {MidiaId}: cabeçalho, tamanho ou duração incoerentes", midia.Id);
            await FalhaDoArquivoAsync(midia, ct);
            return;
        }

        var duracaoMin = limite.DuracaoMinima.TotalSeconds;
        var duracaoMax = limite.DuracaoMaxima.TotalSeconds;
        if ((double)duracao.Value < duracaoMin || (double)duracao.Value > duracaoMax)
        {
            await RejeitarAsync(midia, $"duração fora do limite de {duracaoMin:0}s a {duracaoMax:0}s para {midia.TipoMidia}", apagarObjeto: true, ct);
            return;
        }

        // A saída ficou na memória (≤ ~29 MB para 20 min): hash e envio saem do MESMO buffer, que é o que foi conferido.
        var hash = SHA256.HashData(saida);
        if (await db.ArquivosMidia.AnyAsync(m => m.HashSHA256 == hash && m.StatusSanitizacao == StatusSanitizacao.Aprovado, ct))
        {
            await RejeitarAsync(midia, MotivoDuplicado, apagarObjeto: true, ct);
            return;
        }

        // 7) só agora a saída entra em midia/{yyyy}/{MM}/{id}.mp3 (o original nunca chega lá).
        var chaveFinal = midia.ChaveStorage + ".mp3";
        await using (var buffer = new MemoryStream(saida, writable: false))
            await storage.PutAsync(Buckets.Midia, chaveFinal, buffer, saida.Length, "audio/mpeg", ct);

        midia.Aprovar("audio/mpeg", hash, duracao.Value, saida.Length, ".mp3", limite, clock.UtcNow);
        Notificar(midia);
        var violouODuplicado = false;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            violouODuplicado = true; // confirmado abaixo: só vale se OUTRA mídia aprovada tem o mesmo hash
        }

        if (violouODuplicado)
        {
            if (!await EhDuplicadaAsync(hash, midia.Id, ct)) throw new InvalidOperationException("Falha ao gravar a aprovação da mídia.");
            // Corrida: outra mídia com a mesma saída foi aprovada entre a conferência e o INSERT (UX_ArquivoMidia_Hash_Aprovado). Vira duplicada.
            await storage.DeleteAsync(Buckets.Midia, chaveFinal, CancellationToken.None);
            await RejeitarAsync(midia, MotivoDuplicado, apagarObjeto: true, ct, jaDescartada: true);
            return;
        }

        // 8) o original sai da quarentena; falha aqui não desfaz a aprovação (a Manutenção apaga o órfão, E4-D11).
        await ApagarOriginalAsync(midia, ct);
        logger.LogInformation("PIPELINE_MIDIA_APROVADA midia {MidiaId} formato {Formato} codec {Codec} duracao {Duracao}", midia.Id, formato, resultado.Codec ?? "-", duracao.Value);
    }

    private async Task<bool> EhDuplicadaAsync(byte[] hash, Guid propriaId, CancellationToken ct)
    {
        // O contexto ficou sujo: a consulta usa o AsNoTracking e só procura OUTRA mídia aprovada com o mesmo hash.
        return await db.ArquivosMidia.AsNoTracking().AnyAsync(m => m.Id != propriaId && m.HashSHA256 == hash && m.StatusSanitizacao == StatusSanitizacao.Aprovado, ct);
    }

    private async Task RejeitarAsync(ArquivoMidia midia, string motivo, bool apagarObjeto, CancellationToken ct, bool jaDescartada = false)
    {
        if (jaDescartada)
        {
            // A aprovação em memória não foi gravada: recarrega o estado do banco (EmAnalise) sem perder o hash do original que o pipeline já calculou.
            var hashOriginal = midia.HashOriginalSHA256;
            await db.ArquivosMidia.Entry(midia).ReloadAsync(ct);
            if (midia.HashOriginalSHA256 is null && hashOriginal is not null) midia.RegistrarHashOriginal(hashOriginal);
        }

        midia.Rejeitar(motivo, clock.UtcNow);
        Notificar(midia);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("PIPELINE_MIDIA_REJEITADA midia {MidiaId}: {Motivo}", midia.Id, motivo);
        if (apagarObjeto) await ApagarOriginalAsync(midia, ct);
    }

    /// <summary>Falha atribuível ao ARQUIVO: conta a tentativa; na 3ª vira Rejeitado ("falha de processamento") e o objeto é apagado.</summary>
    private async Task FalhaDoArquivoAsync(ArquivoMidia midia, CancellationToken ct)
    {
        midia.RegistrarFalhaProcessamento(clock.UtcNow);
        var rejeitada = midia.StatusSanitizacao == StatusSanitizacao.Rejeitado;
        if (rejeitada) Notificar(midia);
        await db.SaveChangesAsync(ct);
        if (rejeitada) await ApagarOriginalAsync(midia, ct);
    }

    /// <summary>Infraestrutura: volta a Pendente e devolve a tentativa. Se nem o banco responde, o lease vence e o item volta pelo próprio relógio.</summary>
    private async Task DevolverAsync(ArquivoMidia midia, CancellationToken ct)
    {
        try
        {
            if (midia.StatusSanitizacao == StatusSanitizacao.EmAnalise) midia.DevolverPorFalhaDeInfraestrutura();
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "PIPELINE_MIDIA_DEVOLUCAO_FALHOU midia {MidiaId}: o lease vence e o item volta sozinho.", midia.Id);
        }
    }

    /// <summary>Apaga o original da quarentena (Rejeitado na hora; Aprovado depois da saída). Nunca derruba o pipeline: o que sobrar vira órfão para a Manutenção.</summary>
    private async Task ApagarOriginalAsync(ArquivoMidia midia, CancellationToken ct)
    {
        try
        {
            // Aprovada: a ChaveStorage já ganhou o ".mp3" e aponta para midia/; o original continua na chave SEM extensão da quarentena.
            var chaveDoOriginal = midia.Bucket == Buckets.Midia ? midia.ChaveStorage[..^".mp3".Length] : midia.ChaveStorage;
            await storage.DeleteAsync(Buckets.Quarentena, chaveDoOriginal, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("PIPELINE_MIDIA_ORIGINAL_NAO_APAGADO midia {MidiaId} ({Tipo}): fica como órfão para a Manutenção", midia.Id, e.GetType().Name);
        }
    }

    private void Notificar(ArquivoMidia midia) =>
        outbox.EnfileirarParaGrupos([GruposDoHub.Locutores], EventosDoOutbox.MidiaSanitizada,
            new { midiaId = midia.Id, statusSanitizacao = midia.StatusSanitizacao.ToString(), motivoRejeicao = midia.MotivoRejeicao });
}

internal static class ResultadoSanitizacaoExtensions
{
    public static decimal? Duracao(this ResultadoSanitizacao r) => r.DuracaoSegundos;
}
