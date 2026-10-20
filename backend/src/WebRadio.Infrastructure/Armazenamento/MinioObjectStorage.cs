using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.Exceptions;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Armazenamento;

/// <summary>Configuração do storage (seção <c>Storage</c>). A credencial é a do <c>svc-api</c>, nunca a root (S-A04).</summary>
public sealed record StorageOptions(string Endpoint, string PublicEndpoint, string AccessKey, string SecretKey, bool UsarSsl = false, string Regiao = "us-east-1")
{
    // O nome "SecretKey" cai na máscara do Serilog; o ToString não imprime a credencial nem com {Obj} sem @.
    public override string ToString() => nameof(StorageOptions);
}

/// <summary>
/// Dois clientes S3 (00-arquitetura, risco ALTO): o interno fala com <c>minio:9000</c>; o público só ASSINA para o host de
/// &lt;origem&gt;/storage (D18) e nunca abre conexão.
/// </summary>
public sealed class MinioObjectStorage : IObjectStorage, IDisposable
{
    private readonly IMinioClient _interno;
    private readonly IMinioClient _publico;
    private readonly Uri _publicEndpoint;
    private readonly string _urlSaudeInterna;
    private readonly IClock _clock;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public MinioObjectStorage(StorageOptions o, IClock clock)
        : this(Criar(o.Endpoint, o.UsarSsl, o), CriarPublico(o), o, clock)
    {
    }

    /// <summary>Costura para teste de unidade com cliente falso (o comportamento do servidor é coberto contra MinIO real).</summary>
    internal MinioObjectStorage(IMinioClient interno, IMinioClient publico, StorageOptions o, IClock clock)
    {
        _clock = clock;
        _publicEndpoint = new Uri(o.PublicEndpoint.TrimEnd('/') + "/");
        _interno = interno;
        _publico = publico;
        _urlSaudeInterna = $"{(o.UsarSsl ? "https" : "http")}://{o.Endpoint}/minio/health/live";
    }

    public async Task<PostPolicyAssinada> CriarPostPolicyAsync(
        string bucket, string chave, string contentType, long tamanhoMaximoBytes, TimeSpan validade, CancellationToken ct = default)
    {
        var expira = _clock.UtcNow.Add(validade);

        // S-M01: tudo "eq" (nada de starts-with), faixa de tamanho, status fixo, sem acl nem x-amz-meta-* livre.
        var policy = new PostPolicy();
        policy.SetBucket(bucket);
        policy.SetKey(chave);
        policy.SetContentType(contentType);
        policy.SetContentRange(1, tamanhoMaximoBytes);
        policy.SetSuccessStatusAction("201");
        policy.SetExpires(expira);

        var (_, campos) = await _publico.PresignedPostPolicyAsync(new PresignedPostPolicyArgs().WithBucket(bucket).WithObject(chave).WithPolicy(policy));

        return new PostPolicyAssinada(new Uri(_publicEndpoint, bucket).ToString(), new Dictionary<string, string>(campos), expira);
    }

    public async Task<string> UrlGetPreAssinadaAsync(
        string bucket, string chave, TimeSpan validade, string? contentType = null, string? contentDisposition = null, CancellationToken ct = default)
    {
        var args = new PresignedGetObjectArgs().WithBucket(bucket).WithObject(chave).WithExpiry((int)validade.TotalSeconds);

        var sobrescritas = new Dictionary<string, string>();
        if (contentType is not null) sobrescritas["response-content-type"] = contentType;
        if (contentDisposition is not null) sobrescritas["response-content-disposition"] = contentDisposition;
        if (sobrescritas.Count > 0) args = args.WithHeaders(sobrescritas);

        // S-A03: assina com o host público e SEM o prefixo /storage; o prefixo entra depois, porque o nginx o remove antes
        // de repassar ao MinIO, que recalcula a assinatura sobre o caminho sem prefixo.
        var assinada = new Uri(await _publico.PresignedGetObjectAsync(args));
        var prefixo = _publicEndpoint.AbsolutePath.TrimEnd('/');
        return $"{assinada.Scheme}://{assinada.Authority}{prefixo}{assinada.AbsolutePath}{assinada.Query}";
    }

    public async Task<InfoObjeto?> HeadAsync(string bucket, string chave, CancellationToken ct = default)
    {
        try
        {
            var stat = await _interno.StatObjectAsync(new StatObjectArgs().WithBucket(bucket).WithObject(chave), ct);
            return new InfoObjeto(stat.Size, stat.ContentType, stat.ETag);
        }
        catch (ObjectNotFoundException)
        {
            return null;
        }
    }

    public async Task MoverAsync(
        string bucketOrigem, string chaveOrigem, string bucketDestino, string chaveDestino, string etagEsperado, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etagEsperado);
        var esperado = etagEsperado.Trim('"');
        var origem = new CopySourceObjectArgs().WithBucket(bucketOrigem).WithObject(chaveOrigem).WithMatchETag(esperado);

        // S-M02, duas armadilhas do SDK (Minio 6.0.4 e 7.0.0, confirmadas contra MinIO real):
        // 1) WithMatchETag é descartado: o PUT da cópia sai só com x-amz-copy-source. O header vai explícito, para o servidor
        //    recusar (412) a cópia de um objeto diferente do verificado, de forma atômica.
        // 2) No Minio 6.x o CopyObjectAsync devolvia normalmente mesmo com o 412 (o 7.x lança PreconditionFailedException, tratada
        //    abaixo). Por isso o destino é conferido ANTES de apagar a origem, seja qual for a versão.
        var copia = new CopyObjectArgs().WithBucket(bucketDestino).WithObject(chaveDestino).WithCopyObjectSource(origem)
            .WithHeaders(new Dictionary<string, string> { ["x-amz-copy-source-if-match"] = esperado });
        var mensagem =
            $"O objeto {bucketOrigem}/{chaveOrigem} mudou depois da verificação: a cópia para {bucketDestino}/{chaveDestino} não confere com o ETag esperado.";
        try
        {
            await _interno.CopyObjectAsync(copia, ct);
        }
        catch (PreconditionFailedException e)
        {
            // Minio 7 passou a lançar o 412 (o 6.x o engolia); em qualquer dos dois o servidor recusou e nada foi copiado.
            throw new EtagDivergenteException(mensagem, e);
        }

        var destino = await HeadAsync(bucketDestino, chaveDestino, ct);
        if (destino?.ETag is null || !string.Equals(destino.ETag.Trim('"'), esperado, StringComparison.OrdinalIgnoreCase))
        {

            // Se algo no caminho descartou o header e a cópia foi feita, o objeto trocado não pode ficar no bucket final.
            // Premissa da interface: a chave de destino é exclusiva deste upload, então apagar o que está nela é seguro.
            if (destino is not null)
            {
                try
                {
                    await _interno.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucketDestino).WithObject(chaveDestino), ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new EtagDivergenteException(mensagem + " A limpeza do destino também falhou: o objeto não verificado continua lá.", e);
                }
            }

            throw new EtagDivergenteException(mensagem);
        }

        await _interno.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucketOrigem).WithObject(chaveOrigem), ct);
    }

    public async Task<bool> VerificarAsync(CancellationToken ct = default)
    {
        try
        {
            using var resposta = await _http.GetAsync(_urlSaudeInterna, ct);
            return resposta.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // timeout do próprio HttpClient
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _interno.Dispose();
        _publico.Dispose();
    }

    private static IMinioClient CriarPublico(StorageOptions o)
    {
        var publico = new Uri(o.PublicEndpoint.TrimEnd('/') + "/");
        return Criar(publico.Authority, publico.Scheme == Uri.UriSchemeHttps, o);
    }

    private static IMinioClient Criar(string endpoint, bool ssl, StorageOptions o) =>
        new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(o.AccessKey, o.SecretKey)
            .WithSSL(ssl)
            .WithRegion(o.Regiao) // com região explícita o SDK assina sem consultar o servidor.
            .Build();
}
