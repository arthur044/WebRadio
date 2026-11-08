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
    // Download do original (até 250 MiB): sem timeout total; o cancelamento vem do token (o pipeline tem o orçamento do lease).
    private readonly HttpClient _download = new() { Timeout = Timeout.InfiniteTimeSpan };

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

        // O SDK devolve só os campos de assinatura. A policy exige (eq) o Content-Type e o success_action_status: sem eles em `campos` o browser, que
        // só repassa o que a API devolve, tomaria 403 em todo upload (Policy Condition failed). Nada além disso entra no form (sem acl nem x-amz-meta-*).
        var formulario = new Dictionary<string, string>(campos) { ["Content-Type"] = contentType, ["success_action_status"] = "201" };
        return new PostPolicyAssinada(new Uri(_publicEndpoint, bucket).ToString(), formulario, expira);
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

    public async Task<long> BaixarAsync(string bucket, string chave, string etagEsperado, long limiteBytes, Stream destino, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etagEsperado);
        var esperado = etagEsperado.Trim('"');

        // GET direto numa URL pré-assinada INTERNA (60 s) com o If-Match explícito: o servidor recusa com 412 e nada é lido. O SDK não serve aqui: descarta o
        // WithMatchETag e, no 412 do GetObject, lança NullReferenceException (Minio 7.0.0, confirmado contra MinIO real). O If-Match não entra nos headers assinados.
        var url = await _interno.PresignedGetObjectAsync(new PresignedGetObjectArgs().WithBucket(bucket).WithObject(chave).WithExpiry(60));
        using var pedido = new HttpRequestMessage(HttpMethod.Get, url);
        pedido.Headers.TryAddWithoutValidation("If-Match", $"\"{esperado}\"");
        using var resposta = await _download.SendAsync(pedido, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resposta.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            throw new EtagDivergenteException($"O objeto {bucket}/{chave} mudou depois do concluir: o ETag não confere.");
        resposta.EnsureSuccessStatusCode();

        // Corte em limiteBytes + 1: o objeto pode ser bem maior que o declarado e não pode encher o tmpfs do job.
        await using var fluxo = await resposta.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920];
        long escritos = 0;
        int lidos;
        while ((lidos = await fluxo.ReadAsync(buffer, ct)) > 0)
        {
            var cabe = (int)Math.Min(lidos, limiteBytes + 1 - escritos);
            await destino.WriteAsync(buffer.AsMemory(0, cabe), ct);
            escritos += cabe;
            if (escritos > limiteBytes) throw new ObjetoGrandeDemaisException($"O objeto {bucket}/{chave} passa de {limiteBytes} bytes.");
        }

        return escritos;
    }

    public async Task PutAsync(string bucket, string chave, Stream conteudo, long tamanhoBytes, string contentType, CancellationToken ct = default) =>
        await _interno.PutObjectAsync(new PutObjectArgs().WithBucket(bucket).WithObject(chave).WithStreamData(conteudo)
            .WithObjectSize(tamanhoBytes).WithContentType(contentType), ct);

    public async Task DeleteAsync(string bucket, string chave, CancellationToken ct = default) =>
        await _interno.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject(chave), ct);

    public async Task<IReadOnlyList<ObjetoListado>> ListarAsync(string bucket, string prefixo, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefixo);
        var itens = new List<ObjetoListado>();
        await foreach (var item in _interno.ListObjectsEnumAsync(new ListObjectsArgs().WithBucket(bucket).WithPrefix(prefixo).WithRecursive(true), ct))
        {
            if (item.IsDir) continue;
            itens.Add(new ObjetoListado(item.Key, DateTime.SpecifyKind(item.LastModifiedDateTime ?? DateTime.MinValue, DateTimeKind.Utc)));
        }

        return itens;
    }

    public string UrlPublica(string bucket, string chave) => new Uri(_publicEndpoint, $"{bucket}/{chave}").ToString();

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
        _download.Dispose();
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
