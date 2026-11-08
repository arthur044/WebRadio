namespace WebRadio.Application.Abstracoes;

/// <summary>Cliente de storage (MinIO/S3). Há dois clientes por trás: o interno (rede) e o público (assina para &lt;origem&gt;/storage, D18).</summary>
public interface IObjectStorage
{
    /// <summary>
    /// POST policy com as condições exatas do S-M01: bucket eq, key eq, content-length-range [1, tamanhoMaximoBytes],
    /// Content-Type eq, success_action_status fixo, sem acl. A URL devolvida é a pública (&lt;origem&gt;/storage/&lt;bucket&gt;).
    /// </summary>
    Task<PostPolicyAssinada> CriarPostPolicyAsync(string bucket, string chave, string contentType, long tamanhoMaximoBytes, TimeSpan validade, CancellationToken ct = default);

    /// <summary>GET pré-assinado pelo cliente público (S-A03): assina sem o prefixo /storage e o insere depois.</summary>
    Task<string> UrlGetPreAssinadaAsync(string bucket, string chave, TimeSpan validade, string? contentType = null, string? contentDisposition = null, CancellationToken ct = default);

    /// <summary>HEAD do objeto; null se não existir.</summary>
    Task<InfoObjeto?> HeadAsync(string bucket, string chave, CancellationToken ct = default);

    /// <summary>
    /// <c>GET</c> CONDICIONAL (E4-F05, S-M02): baixa o objeto direto para <paramref name="destino"/> com <c>If-Match: &lt;etag&gt;</c>. Se o objeto mudou depois de o
    /// <c>concluir</c> registrar o ETag (a POST policy ainda aceita outro POST na mesma chave), o servidor responde 412 e isto lança <see cref="EtagDivergenteException"/>
    /// sem escrever nada. Corta em <paramref name="limiteBytes"/> + 1: passou do limite, lança <see cref="ObjetoGrandeDemaisException"/> (o destino pode ter até
    /// <paramref name="limiteBytes"/> + 1 bytes; quem chama apaga o arquivo). Devolve os bytes escritos.
    /// </summary>
    Task<long> BaixarAsync(string bucket, string chave, string etagEsperado, long limiteBytes, Stream destino, CancellationToken ct = default);

    /// <summary>Sonda de saúde: o servidor de storage está de pé? Não valida a credencial de serviço.</summary>
    Task<bool> VerificarAsync(CancellationToken ct = default);

    /// <summary>
    /// Grava o objeto (E3-F06: a imagem de divulgação já recodificada). O <c>Content-Type</c> é o do SERVIDOR, nunca o do cliente. Sobrescreve
    /// quem já existe na chave; quem chama usa chave nova (GUID).
    /// </summary>
    Task PutAsync(string bucket, string chave, Stream conteudo, long tamanhoBytes, string contentType, CancellationToken ct = default);

    /// <summary>Apaga o objeto. Chave inexistente não é erro (o S3 também não reclama).</summary>
    Task DeleteAsync(string bucket, string chave, CancellationToken ct = default);

    /// <summary>
    /// Lista por prefixo (recursivo), com a data da última modificação. Usada pela limpeza das imagens órfãs (E3-D6): o prefixo é
    /// obrigatório, para a credencial do serviço poder ter <c>ListBucket</c> só com <c>s3:prefix</c>.
    /// </summary>
    Task<IReadOnlyList<ObjetoListado>> ListarAsync(string bucket, string prefixo, CancellationToken ct = default);

    /// <summary>URL pública do objeto de um bucket de leitura anônima (<c>publico</c>): <c>&lt;origem&gt;/storage/&lt;bucket&gt;/&lt;chave&gt;</c>, sem assinatura.</summary>
    string UrlPublica(string bucket, string chave);
}

public sealed record ObjetoListado(string Chave, DateTime UltimaModificacaoUtc);

public sealed record PostPolicyAssinada(string Url, IReadOnlyDictionary<string, string> Campos, DateTime ExpiraEmUtc);

public sealed record InfoObjeto(long TamanhoBytes, string? ContentType, string? ETag);

/// <summary>O objeto mudou entre o <c>concluir</c> e o <see cref="IObjectStorage.BaixarAsync"/> (S-M02): trate como Rejeitado.</summary>
public sealed class EtagDivergenteException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>O objeto passou do limite pedido no <see cref="IObjectStorage.BaixarAsync"/>.</summary>
public sealed class ObjetoGrandeDemaisException(string message) : Exception(message);

public static class Buckets
{
    public const string Quarentena = "quarentena";
    public const string Midia = "midia";
    public const string Publico = "publico";
}
