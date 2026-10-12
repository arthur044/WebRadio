using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebRadio.Application.Abstracoes;
using Xunit;

namespace WebRadio.Api.IntegrationTests;

/// <summary>
/// E1-F08. O SQL é o banco em memória da ApiFactory (sempre "conecta"). O Redis é um host inalcançável de verdade
/// (multiplexer real, AbortOnConnectFail=false), e o MinIO é um IObjectStorage falso. Redis e MinIO reais ficam para o CI.
/// </summary>
public class HealthTests
{
    private sealed class StorageFalso(bool dePe) : IObjectStorage
    {
        public Task<bool> VerificarAsync(CancellationToken ct = default) => Task.FromResult(dePe);

        public Task<PostPolicyAssinada> CriarPostPolicyAsync(string bucket, string chave, string contentType, long tamanhoMaximoBytes, TimeSpan validade, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string> UrlGetPreAssinadaAsync(string bucket, string chave, TimeSpan validade, string? contentType = null, string? contentDisposition = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<InfoObjeto?> HeadAsync(string bucket, string chave, CancellationToken ct = default) => throw new NotSupportedException();

        public Task MoverAsync(string bucketOrigem, string chaveOrigem, string bucketDestino, string chaveDestino, string etagEsperado, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static ApiFactory ComStorage()
    {
        var f = new ApiFactory();
        f.Config["Storage:Endpoint"] = "127.0.0.1:1";
        f.Config["Storage:PublicEndpoint"] = "http://localhost:8080/storage";
        f.Config["Storage:AccessKey"] = "svc-api-acesso";
        f.Config["Storage:SecretKey"] = new string('s', 32);
        return f;
    }

    private static ApiFactory ComRedisForaDoAr(ApiFactory f)
    {
        f.Config["Redis:Host"] = "127.0.0.1";
        f.Config["Redis:Porta"] = "1";
        f.Config["Redis:Password"] = new string('r', 32);
        return f;
    }

    private static HttpClient Cliente(ApiFactory f, bool? storageDePe = null)
        => f.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            if (storageDePe is { } dePe) s.Replace(ServiceDescriptor.Singleton<IObjectStorage>(new StorageFalso(dePe)));
        })).CreateClient(new() { BaseAddress = new Uri("http://localhost") });

    private static async Task<(HttpStatusCode Status, JsonElement Corpo)> Get(HttpClient c, string rota, int porta = 8081)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, rota);
        req.Headers.Add("X-Test-LocalPort", porta.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var r = await c.SendAsync(req);
        return (r.StatusCode, r.StatusCode == HttpStatusCode.NotFound ? default : await r.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task Ready_com_tudo_de_pe_e_200_Healthy_e_lista_as_tres_dependencias()
    {
        // Sem Redis configurado o check "redis" nem existe; aqui só SQL e MinIO.
        var (status, corpo) = await Get(Cliente(ComStorage(), storageDePe: true), "/health/ready");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", corpo.GetProperty("status").GetString());
        Assert.Equal("Healthy", corpo.GetProperty("checks").GetProperty("sql").GetString());
        Assert.Equal("Healthy", corpo.GetProperty("checks").GetProperty("minio").GetString());
        Assert.False(corpo.GetProperty("checks").TryGetProperty("redis", out _));
    }

    [Fact]
    public async Task Redis_fora_do_ar_deixa_ready_Degraded_com_200_e_nao_503()
    {
        var (status, corpo) = await Get(Cliente(ComRedisForaDoAr(ComStorage()), storageDePe: true), "/health/ready");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Degraded", corpo.GetProperty("status").GetString());
        Assert.Equal("Degraded", corpo.GetProperty("checks").GetProperty("redis").GetString());
        Assert.Equal("Healthy", corpo.GetProperty("checks").GetProperty("sql").GetString());
    }

    [Fact]
    public async Task MinIO_fora_do_ar_deixa_ready_Unhealthy_com_503()
    {
        var (status, corpo) = await Get(Cliente(ComStorage(), storageDePe: false), "/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("Unhealthy", corpo.GetProperty("checks").GetProperty("minio").GetString());
    }

    [Fact]
    public async Task MinIO_real_inalcancavel_tambem_e_503_sem_vazar_detalhe_do_erro()
    {
        // Sem substituir o IObjectStorage: é o MinioObjectStorage real apontando para uma porta fechada.
        var c = Cliente(ComStorage());
        var req = new HttpRequestMessage(HttpMethod.Get, "/health/ready");
        req.Headers.Add("X-Test-LocalPort", "8081");
        var r = await c.SendAsync(req);
        var texto = await r.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.DoesNotContain("127.0.0.1", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", texto, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_nao_consulta_dependencia_nenhuma()
    {
        var (status, corpo) = await Get(Cliente(ComRedisForaDoAr(ComStorage()), storageDePe: false), "/health/live");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", corpo.GetProperty("status").GetString());
        Assert.Empty(corpo.GetProperty("checks").EnumerateObject());
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health/live")]
    public async Task Pela_porta_publica_as_rotas_de_saude_sao_404(string rota)
    {
        var c = Cliente(ComStorage(), storageDePe: true);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(c, rota, porta: 8080)).Status);

        // Host enganoso não muda nada: a checagem é pela porta local do socket.
        var req = new HttpRequestMessage(HttpMethod.Get, rota);
        req.Headers.Host = "localhost:8081";
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Saude_nao_exige_token()
        => Assert.Equal(HttpStatusCode.OK, (await Get(Cliente(new ApiFactory()), "/health/ready")).Status);
}
