using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Armazenamento;
using WebRadio.Infrastructure.DependencyInjection;
using WebRadio.Infrastructure.Seguranca;
using WebRadio.Infrastructure.Tests.Suporte;
using WebRadio.Infrastructure.Triagem;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>E1-F09. Assinatura é local (região explícita): nada aqui abre conexão com o MinIO.</summary>
public class MinioObjectStorageTests
{
    private static readonly DateTime Agora = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    private static MinioObjectStorage Novo(string endpoint = "minio:9000", string publico = "https://radio.example.com/storage") =>
        new(new StorageOptions(endpoint, publico, "svc-api-acesso", "segredo-de-teste-do-svc-api-123"), new RelogioFalso(Agora));

    [Fact]
    public async Task PostPolicy_tem_so_condicoes_exatas_do_S_M01()
    {
        using var storage = Novo();

        var assinada = await storage.CriarPostPolicyAsync(Buckets.Quarentena, "2026/09/abc", "audio/mpeg", 250_000_000, TimeSpan.FromMinutes(15));

        Assert.Equal("https://radio.example.com/storage/quarentena", assinada.Url);
        Assert.Equal(Agora.AddMinutes(15), assinada.ExpiraEmUtc);
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
        Assert.Equal(Agora.AddMinutes(15), expiracao.ToUniversalTime());
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
    public async Task Mover_sem_etag_esperado_e_recusado_antes_de_tocar_no_storage()
    {
        using var storage = Novo(endpoint: "127.0.0.1:1");

        await Assert.ThrowsAnyAsync<ArgumentException>(() => storage.MoverAsync(Buckets.Quarentena, "a", Buckets.Midia, "b", " "));
    }

    [Fact]
    public async Task Health_do_storage_inalcancavel_e_false_sem_lancar()
    {
        using var storage = Novo(endpoint: "127.0.0.1:1", publico: "http://localhost:8080/storage");

        Assert.False(await storage.VerificarAsync());
    }

    [Fact]
    public void Opcoes_do_storage_nao_imprimem_a_credencial()
        => Assert.DoesNotContain("segredo", new StorageOptions("minio:9000", "http://x/storage", "acesso", "segredo-123").ToString(), StringComparison.Ordinal);
}

public class RegistroDeInfraTests
{
    private sealed class Ambiente(string nome) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nome;

        public string ApplicationName { get; set; } = "teste";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Chave, string? Valor)[] extras)
    {
        var pepper = System.Buffers.Text.Base64Url.EncodeToString(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var valores = new Dictionary<string, string?>
        {
            ["ConnectionStrings:RadioDb"] = "Server=x;Database=y;User Id=u;Password=senha-de-teste-123",
            ["Seguranca:Pepper"] = pepper,
            ["Jwt:SigningKey"] = pepper,
        };
        foreach (var (chave, valor) in extras) valores[chave] = valor;
        return new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
    }

    private static ServiceCollection Servicos(IConfiguration configuracao)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuracao);
        services.AddInfrastructure(configuracao);
        return services;
    }

    [Theory]
    [InlineData("Development", "true", true)]
    [InlineData("Development", "True", true)]
    [InlineData("Development", null, false)]
    [InlineData("Development", "false", false)]
    [InlineData("Development", "1", false)]
    [InlineData("Production", "true", false)]
    [InlineData("Staging", "true", false)]
    public void NoOp_so_com_Development_E_PermitirScannerNoOp(string ambiente, string? permitir, bool esperado)
    {
        var configuracao = Config(("Seguranca:PermitirScannerNoOp", permitir));
        var services = Servicos(configuracao).AddScannerNoOpSePermitido(configuracao, new Ambiente(ambiente));

        Assert.Equal(esperado, services.Any(s => s.ServiceType == typeof(IMalwareScanner)));
    }

    [Fact]
    public async Task NoOp_nunca_devolve_Limpo()
        => Assert.Equal(ResultadoScan.Ignorado, await new NoOpMalwareScanner(NullLogger<NoOpMalwareScanner>.Instance).EscanearAsync(new MemoryStream([1, 2, 3])));

    [Fact]
    public void Sem_Storage_Endpoint_nao_ha_IObjectStorage_e_com_ele_resolve_o_Minio()
    {
        Assert.DoesNotContain(Servicos(Config()), s => s.ServiceType == typeof(IObjectStorage));

        using var sp = Servicos(Config(
            ("Storage:Endpoint", "minio:9000"), ("Storage:PublicEndpoint", "http://localhost:8080/storage"),
            ("Storage:AccessKey", "svc-api-acesso"), ("Storage:SecretKey", "segredo-de-teste-do-svc-api-123"))).BuildServiceProvider();
        Assert.IsType<MinioObjectStorage>(sp.GetRequiredService<IObjectStorage>());
    }

    [Fact]
    public void Sem_Redis_o_throttle_e_em_memoria_e_com_Redis_e_o_do_Redis()
    {
        using var semRedis = Servicos(Config()).BuildServiceProvider();
        Assert.IsType<MemoryRequestThrottle>(semRedis.GetRequiredService<IRequestThrottle>());

        Assert.Contains(Servicos(Config(("Redis:Host", "redis"), ("Redis:Password", new string('r', 32)))),
            s => s.ServiceType == typeof(IRequestThrottle) && s.ImplementationType == typeof(RedisRequestThrottle));
    }

    [Fact]
    public void Abstracoes_sem_estado_por_requisicao_sao_singleton()
    {
        var services = Servicos(Config(("Storage:Endpoint", "minio:9000")));

        foreach (var tipo in new[] { typeof(IClock), typeof(IListenerIdentity), typeof(IRequestThrottle), typeof(IObjectStorage) })
            Assert.Equal(ServiceLifetime.Singleton, services.Single(s => s.ServiceType == tipo).Lifetime);
    }
}

public class ListenerIdentityTests
{
    private static readonly byte[] Pepper = Enumerable.Range(0, 32).Select(i => (byte)(i * 5)).ToArray();

    private static ListenerIdentity Nova(byte[]? pepper = null) => new(pepper ?? Pepper, new IpHasher(pepper ?? Pepper));

    [Fact]
    public void DeviceHash_e_estavel_tem_32_bytes_e_muda_com_o_id_e_com_o_pepper()
    {
        var id = Guid.NewGuid();
        var hash = Nova().DeviceHash(id);

        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, Nova().DeviceHash(id));
        Assert.NotEqual(hash, Nova().DeviceHash(Guid.NewGuid()));
        Assert.NotEqual(hash, Nova(Enumerable.Repeat((byte)9, 32).ToArray()).DeviceHash(id));
    }

    [Fact]
    public void Grupo_do_listener_e_listener_mais_o_hash_em_hex_sem_o_id_em_claro()
    {
        var id = Guid.NewGuid();
        var grupo = Nova().GrupoListener(id);

        Assert.Equal("listener:" + Convert.ToHexString(Nova().DeviceHash(id)), grupo);
        Assert.DoesNotContain(id.ToString("N"), grupo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IpHash_e_o_mesmo_do_IpHasher_e_agrupa_o_64_do_IPv6()
    {
        var identidade = Nova();

        Assert.Equal(new IpHasher(Pepper).Hash(IPAddress.Parse("203.0.113.7")), identidade.IpHash(IPAddress.Parse("203.0.113.7")));
        Assert.Equal(identidade.IpHash(IPAddress.Parse("2001:db8:1:2::1")), identidade.IpHash(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.NotEqual(identidade.IpHash(IPAddress.Parse("2001:db8:1:2::1")), identidade.IpHash(IPAddress.Parse("2001:db8:1:3::1")));
    }

    [Fact]
    public void Hash_de_dispositivo_nunca_coincide_com_o_de_um_valor_de_outro_dominio()
    {
        // 16 bytes de um Guid também são um IPv6 válido: o prefixo de domínio impede que os dois HMACs coincidam.
        var id = Guid.NewGuid();
        var comoIp = new IPAddress(id.ToByteArray());

        Assert.NotEqual(Nova().DeviceHash(id), Nova().IpHash(comoIp));
    }
}

public class MemoryRequestThrottleTests
{
    private static readonly TimeSpan Janela = TimeSpan.FromSeconds(30);

    private static (MemoryRequestThrottle Throttle, RelogioFalso Relogio) Novo()
    {
        var relogio = new RelogioFalso(new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc));
        return (new MemoryRequestThrottle(relogio), relogio);
    }

    [Fact]
    public async Task Reserva_e_SET_NX_EX_so_a_primeira_ganha_ate_expirar()
    {
        var (t, relogio) = Novo();

        Assert.True(await t.TentarReservarAsync("pedido:a", Janela));
        Assert.False(await t.TentarReservarAsync("pedido:a", Janela));
        Assert.True(await t.TentarReservarAsync("pedido:b", Janela));

        relogio.Avancar(Janela);
        Assert.True(await t.TentarReservarAsync("pedido:a", Janela));
    }

    [Fact]
    public async Task Incremento_conta_em_janela_fixa_o_TTL_nasce_na_primeira_contagem_e_nao_e_renovado()
    {
        var (t, relogio) = Novo();

        Assert.Equal(1, await t.IncrementarAsync("c", Janela));
        relogio.Avancar(TimeSpan.FromSeconds(20));
        Assert.Equal(2, await t.IncrementarAsync("c", Janela));
        Assert.Equal(TimeSpan.FromSeconds(10), await t.TempoRestanteAsync("c"));
        Assert.Equal(2, await t.LerAsync("c"));

        relogio.Avancar(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await t.LerAsync("c"));
        Assert.Null(await t.TempoRestanteAsync("c"));
        Assert.Equal(1, await t.IncrementarAsync("c", Janela));
    }

    [Fact]
    public async Task Remover_libera_a_chave_e_chave_inexistente_le_zero()
    {
        var (t, _) = Novo();

        await t.TentarReservarAsync("x", Janela);
        await t.RemoverAsync("x");
        await t.RemoverAsync("nunca-existiu");

        Assert.Equal(0, await t.LerAsync("x"));
        Assert.True(await t.TentarReservarAsync("x", Janela));
    }

    [Fact]
    public async Task Incrementos_concorrentes_nao_perdem_contagem()
    {
        var (t, _) = Novo();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => t.IncrementarAsync("c", Janela))));

        Assert.Equal(200, await t.LerAsync("c"));
    }

    [Fact]
    public async Task Cheio_descarta_as_vencidas_e_so_falha_se_ainda_nao_couber()
    {
        var (t, relogio) = Novo();
        for (var i = 0; i < MemoryRequestThrottle.LimiteDeChaves; i++) await t.TentarReservarAsync($"k{i}", Janela);

        await Assert.ThrowsAsync<InvalidOperationException>(() => t.TentarReservarAsync("nova", Janela));
        // Chave que já existe continua contando mesmo com o mapa cheio.
        Assert.Equal(2, await t.IncrementarAsync("k0", Janela));

        relogio.Avancar(Janela);
        Assert.True(await t.TentarReservarAsync("nova", Janela));
    }
}
