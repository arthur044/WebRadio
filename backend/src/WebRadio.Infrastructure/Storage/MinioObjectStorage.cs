using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.Exceptions;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Opcoes;

namespace WebRadio.Infrastructure.Storage;

/// <summary>
/// Dois clientes S3 (00-arquitetura, risco ALTO): o interno fala com minio:9000; o público assina para o host de
/// &lt;origem&gt;/storage (D18). Os dois usam a credencial svc-api (S-A04), nunca a root.
/// </summary>
public sealed class MinioObjectStorage : IObjectStorage, IDisposable
{
    private readonly IMinioClient _interno;
    private readonly IMinioClient _publico;
    private readonly Uri _publicEndpoint;
    private readonly string _urlSaudeInterna;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public MinioObjectStorage(IOptions<StorageOptions> opcoes)
    {
        var o = opcoes.Value;
        _publicEndpoint = new Uri(o.PublicEndpoint.TrimEnd('/') + "/");

        _interno = Criar(o.Endpoint, o.UsarSsl, o);
        _publico = Criar(_publicEndpoint.Authority, _publicEndpoint.Scheme == Uri.UriSchemeHttps, o);
        _urlSaudeInterna = $"{(o.UsarSsl ? "https" : "http")}://{o.Endpoint}/minio/health/live";
    }

    public async Task<PostPolicyAssinada> CriarPostPolicyAsync(
        string bucket, string chave, string contentType, long tamanhoMaximoBytes, TimeSpan validade, CancellationToken cancellationToken = default)
    {
        var expira = DateTime.UtcNow.Add(validade);

        // S-M01: tudo "eq" (nada de starts-with), faixa de tamanho, status fixo, sem acl nem x-amz-meta-* livre.
        var policy = new PostPolicy();
        policy.SetBucket(bucket);
        policy.SetKey(chave);
        policy.SetContentType(contentType);
        policy.SetContentRange(1, tamanhoMaximoBytes);
        policy.SetSuccessStatusAction("201");
        policy.SetExpires(expira);

        var (_, campos) = await _publico.PresignedPostPolicyAsync(new PresignedPostPolicyArgs().WithBucket(bucket).WithObject(chave).WithPolicy(policy));

        var url = new Uri(_publicEndpoint, bucket).ToString();
        return new PostPolicyAssinada(url, new Dictionary<string, string>(campos), expira);
    }

    public async Task<string> UrlGetPreAssinadaAsync(
        string bucket, string chave, TimeSpan validade, string? contentType = null, string? contentDisposition = null, CancellationToken cancellationToken = default)
    {
        var args = new PresignedGetObjectArgs()
            .WithBucket(bucket)
            .WithObject(chave)
            .WithExpiry((int)validade.TotalSeconds);

        var sobrescritas = new Dictionary<string, string>();
        if (contentType is not null)
        {
            sobrescritas["response-content-type"] = contentType;
        }

        if (contentDisposition is not null)
        {
            sobrescritas["response-content-disposition"] = contentDisposition;
        }

        if (sobrescritas.Count > 0)
        {
            args = args.WithHeaders(sobrescritas);
        }

        // S-A03: assina com o host público e SEM o prefixo /storage; o prefixo entra depois, porque o nginx o remove antes
        // de repassar ao MinIO, que recalcula a assinatura sobre o caminho sem prefixo.
        var assinada = new Uri(await _publico.PresignedGetObjectAsync(args));
        var prefixo = _publicEndpoint.AbsolutePath.TrimEnd('/');
        return $"{assinada.Scheme}://{assinada.Authority}{prefixo}{assinada.AbsolutePath}{assinada.Query}";
    }

    public async Task<InfoObjeto?> HeadAsync(string bucket, string chave, CancellationToken cancellationToken = default)
    {
        try
        {
            var stat = await _interno.StatObjectAsync(new StatObjectArgs().WithBucket(bucket).WithObject(chave), cancellationToken);
            return new InfoObjeto(stat.Size, stat.ContentType, stat.ETag);
        }
        catch (ObjectNotFoundException)
        {
            return null;
        }
    }

    public async Task MoverAsync(
        string bucketOrigem, string chaveOrigem, string bucketDestino, string chaveDestino, string etagEsperado, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etagEsperado);
        var origem = new CopySourceObjectArgs().WithBucket(bucketOrigem).WithObject(chaveOrigem);
        origem = origem.WithMatchETag(etagEsperado.Trim('"'));

        await _interno.CopyObjectAsync(
            new CopyObjectArgs().WithBucket(bucketDestino).WithObject(chaveDestino).WithCopyObjectSource(origem),
            cancellationToken);

        await _interno.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucketOrigem).WithObject(chaveOrigem), cancellationToken);
    }

    public async Task<bool> VerificarAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var resposta = await _http.GetAsync(_urlSaudeInterna, cancellationToken);
            return resposta.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
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

    private static IMinioClient Criar(string endpoint, bool ssl, StorageOptions o) =>
        new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(o.AccessKey, o.SecretKey)
            .WithSSL(ssl)
            .WithRegion(o.Regiao) // com região explícita o SDK assina sem consultar o servidor.
            .Build();
}
