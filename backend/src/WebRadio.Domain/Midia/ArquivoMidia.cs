using WebRadio.Domain.Common;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;

namespace WebRadio.Domain.Midia;

/// <summary>
/// schema midia (MediaUpload). 01-dominio.md §2.5, v1.1.
/// Invariante: Aprovado ⇒ MimeType, HashSHA256, DuracaoSegundos, SanitizadoEmUtc ≠ null (reforçado por CHECK no banco).
/// </summary>
public sealed class ArquivoMidia : Entidade
{
    public const int TamanhoHashBytes = 32;

    /// <summary>Fluxo C: na 3ª falha de processamento (não determinística — timeout/crash do sanitizer) → Rejeitado.</summary>
    public const byte MaxTentativasSanitizacao = 3;

    private const string BucketQuarentena = "quarentena";
    private const string BucketMidia = "midia";

    /// <summary>CK_ArquivoMidia_MimeType.</summary>
    private static readonly string[] MimeTypesPermitidos = ["audio/mpeg", "audio/ogg", "audio/wav", "audio/flac"];

    public string NomeOriginal { get; private set; } = null!;

    public string? Titulo { get; private set; }

    public string? Artista { get; private set; }

    public TipoMidia TipoMidia { get; private set; }

    public string Bucket { get; private set; } = null!;

    /// <summary>Gerada pelo servidor a partir de Id + data de criação: `{yyyy}/{MM}/{Id}` (D11/S-M01); ganha a
    /// extensão em Aprovar, após a recodificação.</summary>
    public string ChaveStorage { get; private set; } = null!;

    public string MimeTypeDeclarado { get; private set; } = null!;

    public string? MimeType { get; private set; }

    public long TamanhoBytes { get; private set; }

    /// <summary>Da saída recodificada (determinística, +bitexact); único entre os Aprovado.</summary>
    public byte[]? HashSHA256 { get; private set; }

    /// <summary>Do arquivo enviado, antes da recodificação — auditoria/reenvio/inteligência de malware; não único.</summary>
    public byte[]? HashOriginalSHA256 { get; private set; }

    /// <summary>ETag do objeto registrado no `concluir`; se mudar antes da análise → Rejeitado (TOCTOU, S-M02).</summary>
    public string? EtagUpload { get; private set; }

    public decimal? DuracaoSegundos { get; private set; }

    public StatusSanitizacao StatusSanitizacao { get; private set; }

    /// <summary>+1 a cada vez que entra em EmAnalise.</summary>
    public byte TentativasSanitizacao { get; private set; }

    /// <summary>Lease do worker; obrigatório em EmAnalise. Um lease vencido conta como falha (mesma regra de
    /// RegistrarFalhaProcessamento) — nunca fica preso em EmAnalise nem estoura a CK 0-10.</summary>
    public DateTime? EmAnaliseDesdeUtc { get; private set; }

    public string? MotivoRejeicao { get; private set; }

    public Guid EnviadoPorUsuarioId { get; private set; }

    public DateTime DataUploadUtc { get; private set; }

    public DateTime UploadExpiraEmUtc { get; private set; }

    public DateTime? SanitizadoEmUtc { get; private set; }

    public bool Ativo { get; private set; }

    private ArquivoMidia()
    {
    }

    public ArquivoMidia(
        Guid id,
        string nomeOriginal,
        string? titulo,
        string? artista,
        TipoMidia tipoMidia,
        string mimeTypeDeclarado,
        long tamanhoBytesDeclarado,
        Guid enviadoPorUsuarioId,
        DateTime agora)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nomeOriginal);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeTypeDeclarado);
        UtcGuard.Exigir(agora, nameof(agora));

        if (tipoMidia == TipoMidia.Musica)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(titulo);
        }

        if (tamanhoBytesDeclarado <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tamanhoBytesDeclarado), "O tamanho declarado precisa ser positivo.");
        }

        NomeOriginal = nomeOriginal;
        Titulo = titulo;
        Artista = artista;
        TipoMidia = tipoMidia;
        Bucket = BucketQuarentena;
        ChaveStorage = $"{agora:yyyy}/{agora:MM}/{id}";
        MimeTypeDeclarado = mimeTypeDeclarado;
        TamanhoBytes = tamanhoBytesDeclarado;
        StatusSanitizacao = StatusSanitizacao.AguardandoUpload;
        EnviadoPorUsuarioId = enviadoPorUsuarioId;
        DataUploadUtc = agora;
        UploadExpiraEmUtc = agora.AddHours(1);
        Ativo = true;
    }

    /// <summary>API: concluir, HEAD ok. Grava o EtagUpload para a checagem de TOCTOU do worker.</summary>
    public void ConcluirUpload(long tamanhoBytesReal, string etag, DateTime agora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etag);
        UtcGuard.Exigir(agora, nameof(agora));
        GarantirStatus(StatusSanitizacao.AguardandoUpload);

        if (tamanhoBytesReal <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tamanhoBytesReal), "O tamanho enviado precisa ser positivo.");
        }

        if (agora >= UploadExpiraEmUtc)
        {
            throw new TransicaoInvalidaException("A janela de upload (1h) expirou.");
        }

        if (tamanhoBytesReal > TamanhoBytes)
        {
            // TamanhoBytes ainda guarda o valor declarado na criação até esta linha o sobrescrever;
            // não usar um campo privado à parte, que não sobreviveria a um reload do EF Core (F04).
            throw new TransicaoInvalidaException("O tamanho enviado é maior que o declarado na criação.");
        }

        TamanhoBytes = tamanhoBytesReal;
        EtagUpload = etag;
        StatusSanitizacao = StatusSanitizacao.Pendente;
    }

    /// <summary>Worker pega o arquivo: EmAnalise, EmAnaliseDesdeUtc = agora (lease), TentativasSanitizacao + 1.</summary>
    public void IniciarAnalise(DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));
        GarantirStatus(StatusSanitizacao.Pendente);

        TentativasSanitizacao++;
        StatusSanitizacao = StatusSanitizacao.EmAnalise;
        EmAnaliseDesdeUtc = agora;
    }

    /// <summary>Um lease vencido (> timeout do job) é uma falha como outra qualquer: mesma regra de 3 tentativas.</summary>
    public void LiberarLeaseExpirado(DateTime agora, TimeSpan timeoutLease)
    {
        UtcGuard.Exigir(agora, nameof(agora));
        GarantirStatus(StatusSanitizacao.EmAnalise);

        if (EmAnaliseDesdeUtc is null || agora - EmAnaliseDesdeUtc.Value < timeoutLease)
        {
            throw new TransicaoInvalidaException("O lease de análise ainda não expirou.");
        }

        TransicaoPorFalha(agora);
    }

    /// <summary>Hash do arquivo recebido, antes da recodificação — auditoria; escrita única, não muda o status.</summary>
    public void RegistrarHashOriginal(byte[] hashOriginalSha256)
    {
        ValidarHash(hashOriginalSha256, nameof(hashOriginalSha256));
        GarantirStatus(StatusSanitizacao.EmAnalise);

        if (HashOriginalSHA256 is not null)
        {
            throw new TransicaoInvalidaException("O hash original já foi registrado.");
        }

        HashOriginalSHA256 = (byte[])hashOriginalSha256.Clone();
    }

    /// <summary>Rejeição determinística: formato inválido, duplicata, TOCTOU do ETag (limite de tamanho/duração
    /// passa por Aprovar, que rejeita internamente em vez de lançar).</summary>
    public void Rejeitar(string motivo, DateTime agora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        UtcGuard.Exigir(agora, nameof(agora));
        GarantirStatus(StatusSanitizacao.EmAnalise);

        StatusSanitizacao = StatusSanitizacao.Rejeitado;
        MotivoRejeicao = motivo;
        SanitizadoEmUtc = agora;
        EmAnaliseDesdeUtc = null;
    }

    /// <summary>
    /// Falha não determinística do sanitizer (crash/timeout): volta para Pendente até a 3ª tentativa; na 3ª,
    /// vira Rejeitado("falha de processamento").
    /// </summary>
    public void RegistrarFalhaProcessamento(DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));
        GarantirStatus(StatusSanitizacao.EmAnalise);

        TransicaoPorFalha(agora);
    }

    public void MarcarQuarentena(string motivo, DateTime agora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        UtcGuard.Exigir(agora, nameof(agora));
        GarantirStatus(StatusSanitizacao.EmAnalise);

        StatusSanitizacao = StatusSanitizacao.Quarentena;
        MotivoRejeicao = motivo;
        SanitizadoEmUtc = agora;
        EmAnaliseDesdeUtc = null;
    }

    /// <summary>
    /// DuracaoSegundos, MimeType e TamanhoBytes vêm do arquivo de saída (S-A01). Mídia fora do limite do tipo
    /// (§2.5) ou com MimeType fora da lista permitida (CK_ArquivoMidia_MimeType) vira Rejeitado com o motivo —
    /// nunca lança exceção, para não confundir o worker com uma falha de processamento transitória (H2).
    /// </summary>
    public void Aprovar(
        string mimeType,
        byte[] hashSha256Saida,
        decimal duracaoSegundos,
        long tamanhoBytesSaida,
        string extensao,
        LimiteMidia limite,
        DateTime agora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(extensao);
        ValidarHash(hashSha256Saida, nameof(hashSha256Saida));
        UtcGuard.Exigir(agora, nameof(agora));
        GarantirStatus(StatusSanitizacao.EmAnalise);

        if (tamanhoBytesSaida <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tamanhoBytesSaida), "O tamanho de saída precisa ser positivo.");
        }

        if (!MimeTypesPermitidos.Contains(mimeType))
        {
            Rejeitar($"tipo de mídia não permitido: {mimeType}", agora);
            return;
        }

        var duracao = TimeSpan.FromSeconds((double)duracaoSegundos);
        if (duracao < limite.DuracaoMinima || duracao > limite.DuracaoMaxima)
        {
            Rejeitar($"duração ({duracao}) fora do limite [{limite.DuracaoMinima}, {limite.DuracaoMaxima}] para {TipoMidia}", agora);
            return;
        }

        if (tamanhoBytesSaida > limite.TamanhoMaximoBytes)
        {
            Rejeitar($"tamanho ({tamanhoBytesSaida} bytes) acima do limite de {limite.TamanhoMaximoBytes} bytes para {TipoMidia}", agora);
            return;
        }

        MimeType = mimeType;
        HashSHA256 = (byte[])hashSha256Saida.Clone();
        DuracaoSegundos = duracaoSegundos;
        TamanhoBytes = tamanhoBytesSaida;
        ChaveStorage += extensao;
        StatusSanitizacao = StatusSanitizacao.Aprovado;
        Bucket = BucketMidia;
        SanitizadoEmUtc = agora;
        EmAnaliseDesdeUtc = null;
    }

    public void Desativar() => Ativo = false;

    private void TransicaoPorFalha(DateTime agora)
    {
        if (TentativasSanitizacao >= MaxTentativasSanitizacao)
        {
            StatusSanitizacao = StatusSanitizacao.Rejeitado;
            MotivoRejeicao = "falha de processamento";
            SanitizadoEmUtc = agora;
        }
        else
        {
            StatusSanitizacao = StatusSanitizacao.Pendente;
        }

        EmAnaliseDesdeUtc = null;
    }

    private void GarantirStatus(StatusSanitizacao esperado)
    {
        if (StatusSanitizacao != esperado)
        {
            throw new TransicaoInvalidaException($"Esperava o status {esperado}, mas a mídia está {StatusSanitizacao}.");
        }
    }

    private static void ValidarHash(byte[] hash, string nomeParametro)
    {
        ArgumentNullException.ThrowIfNull(hash, nomeParametro);

        if (hash.Length != TamanhoHashBytes)
        {
            throw new ArgumentException($"O hash precisa ter {TamanhoHashBytes} bytes (SHA-256).", nomeParametro);
        }
    }
}
