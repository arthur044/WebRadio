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
    /// Copia e apaga a origem. <paramref name="etagEsperado"/> é obrigatório: a cópia falha se o objeto mudou depois da
    /// verificação (S-M02: a POST policy ainda aceita novo POST na mesma key). Com ETag divergente lança
    /// <see cref="EtagDivergenteException"/>, o destino não recebe o objeto e a origem continua intacta.
    /// </summary>
    Task MoverAsync(string bucketOrigem, string chaveOrigem, string bucketDestino, string chaveDestino, string etagEsperado, CancellationToken ct = default);

    /// <summary>Sonda de saúde: o servidor de storage está de pé? Não valida a credencial de serviço.</summary>
    Task<bool> VerificarAsync(CancellationToken ct = default);
}

public sealed record PostPolicyAssinada(string Url, IReadOnlyDictionary<string, string> Campos, DateTime ExpiraEmUtc);

public sealed record InfoObjeto(long TamanhoBytes, string? ContentType, string? ETag);

/// <summary>O objeto mudou entre a verificação e o <see cref="IObjectStorage.MoverAsync"/> (S-M02): trate como Rejeitado.</summary>
public sealed class EtagDivergenteException(string message) : Exception(message);

public static class Buckets
{
    public const string Quarentena = "quarentena";
    public const string Midia = "midia";
    public const string Publico = "publico";
}
