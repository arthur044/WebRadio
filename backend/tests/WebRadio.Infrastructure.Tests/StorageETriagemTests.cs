using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.DependencyInjection;
using WebRadio.Infrastructure.Opcoes;
using WebRadio.Infrastructure.Scanner;
using WebRadio.Infrastructure.Storage;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

public class MinioObjectStorageTests
{
    private static MinioObjectStorage Novo() => new(Options.Create(new StorageOptions
    {
        Endpoint = "minio:9000",
        PublicEndpoint = "https://radio.example.com/storage",
        AccessKey = "svc-api-acesso",
        SecretKey = "segredo-de-teste-do-svc-api-123",
    }));

    [Fact]
    public async Task PostPolicy_tem_so_condicoes_exatas_do_S_M01()
    {
        using var storage = Novo();

        var assinada = await storage.CriarPostPolicyAsync(Buckets.Quarentena, "2026/09/abc", "audio/mpeg", 250_000_000, TimeSpan.FromMinutes(15));

        Assert.Equal("https://radio.example.com/storage/quarentena", assinada.Url);
        var politica = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(assinada.Campos["policy"])));
        var condicoes = politica.RootElement.GetProperty("conditions").EnumerateArray().Select(c => c.ToString()).ToList();

        Assert.Contains(condicoes, c => c.Contains("\"eq\"") && c.Contains("$bucket") && c.Contains("quarentena"));
        Assert.Contains(condicoes, c => c.Contains("\"eq\"") && c.Contains("$key") && c.Contains("2026/09/abc"));
        Assert.Contains(condicoes, c => c.Contains("\"eq\"") && c.Contains("$Content-Type") && c.Contains("audio/mpeg"));
        Assert.Contains(condicoes, c => c.Contains("content-length-range") && c.Contains("250000000"));
        Assert.Contains(condicoes, c => c.Contains("$success_action_status") && c.Contains("201"));

        // Nada de starts-with (um locutor sobrescreveria o upload de outro), acl ou metadados livres.
        Assert.DoesNotContain(condicoes, c => c.Contains("starts-with", StringComparison.Ordinal));
        Assert.DoesNotContain(condicoes, c => c.Contains("acl", StringComparison.OrdinalIgnoreCase) || c.Contains("x-amz-meta", StringComparison.OrdinalIgnoreCase));

        var expiracao = DateTime.Parse(politica.RootElement.GetProperty("expiration").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.InRange(expiracao, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));
        Assert.False(assinada.Campos.ContainsKey("x-amz-security-token"));
    }

    [Fact]
    public async Task Get_pre_assinado_usa_o_host_publico_e_insere_o_prefixo_storage_depois_de_assinar()
    {
        using var storage = Novo();

        var url = new Uri(await storage.UrlGetPreAssinadaAsync(
            Buckets.Midia, "2026/09/faixa", TimeSpan.FromMinutes(5), "audio/mpeg", "inline; filename*=UTF-8''abc.mp3"));

        Assert.Equal("radio.example.com", url.Host);
        Assert.Equal("/storage/midia/2026/09/faixa", url.AbsolutePath);
        Assert.Contains("X-Amz-Signature=", url.Query, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Expires=300", url.Query, StringComparison.Ordinal);
        Assert.Contains("response-content-type=audio%2Fmpeg", url.Query, StringComparison.Ordinal);
        Assert.Contains("response-content-disposition=", url.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("minio", url.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_do_storage_inalcancavel_e_false_sem_lancar()
    {
        using var storage = new MinioObjectStorage(Options.Create(new StorageOptions
        {
            Endpoint = "127.0.0.1:1",
            PublicEndpoint = "http://localhost:8080/storage",
            AccessKey = "svc-api-acesso",
            SecretKey = "segredo-de-teste-do-svc-api-123",
        }));

        Assert.False(await storage.VerificarAsync());
    }
}

public class RegistroDoScannerNoOpTests
{
    private sealed class Ambiente(string nome) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nome;

        public string ApplicationName { get; set; } = "teste";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static bool NoOpRegistrado(string ambiente, string? permitir)
    {
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RadioDb"] = "Server=x;Database=y;User Id=u;Password=senha-de-teste-123",
            ["Seguranca:PermitirScannerNoOp"] = permitir,
        }).Build();

        var services = new ServiceCollection();
        services.AddInfrastructureApi(configuracao, new Ambiente(ambiente));
        return services.Any(s => s.ServiceType == typeof(IMalwareScanner) && s.ImplementationType == typeof(NoOpMalwareScanner));
    }

    [Theory]
    [InlineData("Development", "true", true)]
    [InlineData("Development", null, false)]
    [InlineData("Development", "false", false)]
    [InlineData("Production", "true", false)]
    [InlineData("Staging", "true", false)]
    public void NoOp_so_com_Development_E_PermitirScannerNoOp(string ambiente, string? permitir, bool esperado) =>
        Assert.Equal(esperado, NoOpRegistrado(ambiente, permitir));

    [Fact]
    public async Task NoOp_nunca_devolve_Limpo()
    {
        var scanner = new NoOpMalwareScanner(Microsoft.Extensions.Logging.Abstractions.NullLogger<NoOpMalwareScanner>.Instance);

        Assert.Equal(ResultadoScan.Ignorado, await scanner.EscanearAsync(new MemoryStream([1, 2, 3])));
    }
}
