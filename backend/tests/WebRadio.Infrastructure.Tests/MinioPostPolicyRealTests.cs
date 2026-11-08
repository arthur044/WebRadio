using System.Net;
using Minio;
using Minio.DataModel.Args;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Armazenamento;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// E4-F02 / S-M01 contra MinIO de verdade: o que a POST policy aceita e recusa. O servidor é quem aplica as condições; o teste de texto
/// (<c>PostPolicy_tem_so_condicoes_exatas_do_S_M01</c>) não prova que o MinIO as faz valer.
/// </summary>
public sealed class MinioPostPolicyRealTests : IAsyncLifetime
{
    private const int Limite = 1000;
    private readonly string _bucket = "quarentena-" + Guid.NewGuid().ToString("N")[..10];
    private readonly string _chave = $"2026/11/{Guid.NewGuid()}";
    private IMinioClient? _admin;
    private MinioObjectStorage? _storage;
    private readonly HttpClient _http = new();

    public async Task InitializeAsync()
    {
        if (!MinioDeTeste.Disponivel) return;

        _admin = new MinioClient().WithEndpoint(MinioDeTeste.Endpoint).WithCredentials(MinioDeTeste.AccessKey, MinioDeTeste.SecretKey).WithSSL(false).WithRegion("us-east-1").Build();
        await _admin.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucket));
        _storage = new MinioObjectStorage(
            new StorageOptions(MinioDeTeste.Endpoint!, "http://" + MinioDeTeste.Endpoint + "/storage", MinioDeTeste.AccessKey!, MinioDeTeste.SecretKey!),
            new RelogioFalso(DateTime.UtcNow));
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        _storage?.Dispose();
        if (_admin is null) return;

        await foreach (var item in _admin.ListObjectsEnumAsync(new ListObjectsArgs().WithBucket(_bucket).WithRecursive(true)))
            await _admin.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_bucket).WithObject(item.Key));
        await _admin.RemoveBucketAsync(new RemoveBucketArgs().WithBucket(_bucket));
        _admin.Dispose();
    }

    /// <summary>O que o browser faz: <c>multipart/form-data</c> com os campos da policy (já alterados pelo teste) e o <c>file</c> por último.</summary>
    private async Task<HttpStatusCode> PostarAsync(Action<Dictionary<string, string>>? alterar = null, int tamanho = 100)
    {
        var policy = await _storage!.CriarPostPolicyAsync(_bucket, _chave, "audio/mpeg", Limite, TimeSpan.FromMinutes(5));
        var campos = new Dictionary<string, string>(policy.Campos);
        alterar?.Invoke(campos);

        using var form = new MultipartFormDataContent();
        foreach (var (k, v) in campos) form.Add(new StringContent(v), k);
        form.Add(new ByteArrayContent(new byte[tamanho]), "file", "x.mp3");
        // Direto no MinIO (sem o nginx e o prefixo /storage): a assinatura é da policy, não do caminho.
        var resposta = await _http.PostAsync($"http://{MinioDeTeste.Endpoint}/{_bucket}", form);
        UltimoCorpo = await resposta.Content.ReadAsStringAsync();
        return resposta.StatusCode;
    }

    private string UltimoCorpo { get; set; } = "";

    [MinioFact]
    public async Task Upload_dentro_da_policy_e_aceito_com_201_e_o_objeto_existe()
    {
        var status = await PostarAsync();
        Assert.True(status == HttpStatusCode.Created, UltimoCorpo);
        Assert.NotNull(await _storage!.HeadAsync(_bucket, _chave));
    }

    [MinioFact]
    public async Task Chave_alterada_no_form_e_403()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await PostarAsync(c => c["key"] = _chave + "x"));
        Assert.Null(await _storage!.HeadAsync(_bucket, _chave + "x"));
    }

    [MinioFact]
    public async Task Content_Type_diferente_e_403()
        => Assert.Equal(HttpStatusCode.Forbidden, await PostarAsync(c => c["Content-Type"] = "text/html"));

    [MinioTheory]
    [InlineData("acl", "public-read")]
    [InlineData("x-amz-meta-origem", "livre")]
    [InlineData("tagging", "<Tagging/>")]
    [InlineData("Content-Disposition", "attachment")]
    [InlineData("x-amz-storage-class", "REDUCED_REDUNDANCY")]
    public async Task Campo_extra_fora_da_policy_e_403(string campo, string valor)
        => Assert.Equal(HttpStatusCode.Forbidden, await PostarAsync(c => c[campo] = valor));

    [MinioFact]
    public async Task Success_action_status_diferente_e_403()
        => Assert.Equal(HttpStatusCode.Forbidden, await PostarAsync(c => c["success_action_status"] = "200"));

    [MinioFact]
    public async Task Arquivo_acima_do_limite_e_recusado_pelo_storage_e_nada_fica_gravado()
    {
        var status = await PostarAsync(tamanho: Limite + 1);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Null(await _storage!.HeadAsync(_bucket, _chave));
    }

    [MinioFact]
    public async Task Arquivo_vazio_e_recusado_pela_faixa_minima()
    {
        Assert.Equal(HttpStatusCode.BadRequest, await PostarAsync(tamanho: 0));
        Assert.Null(await _storage!.HeadAsync(_bucket, _chave));
    }
}
