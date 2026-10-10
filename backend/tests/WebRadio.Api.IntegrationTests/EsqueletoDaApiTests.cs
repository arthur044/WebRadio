using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Xunit;

namespace WebRadio.Api.IntegrationTests;

/// <summary>E1-F06 (esqueleto), E1-F08 (health só na :8081) e a parte de F07 que não precisa de banco.</summary>
public class EsqueletoDaApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _fabrica;

    public EsqueletoDaApiTests(ApiFactory fabrica) => _fabrica = fabrica;

    [Fact]
    public async Task Erro_de_validacao_vira_400_ProblemDetails_com_errors()
    {
        var cliente = _fabrica.CreateClient();

        var resposta = await cliente.PostAsJsonAsync("/api/v1/auth/login", new { });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
        var json = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/erros/validacao", json.GetProperty("type").GetString());
        var erros = json.GetProperty("errors");
        Assert.True(erros.TryGetProperty("email", out _));
        Assert.True(erros.TryGetProperty("senha", out _));
    }

    [Fact]
    public async Task Cadastro_com_senha_curta_ou_vazada_e_recusado_na_borda()
    {
        var cliente = _fabrica.CreateClient();

        var curta = await cliente.PostAsJsonAsync("/api/v1/auth/registrar", new { nome = "Ana", email = "ana@example.com", senha = "curta" });
        var vazada = await cliente.PostAsJsonAsync("/api/v1/auth/registrar", new { nome = "Ana", email = "ana@example.com", senha = "password1234" });

        Assert.Equal(HttpStatusCode.BadRequest, curta.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, vazada.StatusCode);
        var erros = (await vazada.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(erros.TryGetProperty("senha", out _));
    }

    [Fact]
    public async Task Rota_protegida_sem_token_responde_401_ProblemDetails()
    {
        var resposta = await _fabrica.CreateClient().GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        var json = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/erros/nao-autenticado", json.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Rota_inexistente_responde_404_ProblemDetails()
    {
        var resposta = await _fabrica.CreateClient().GetAsync("/nao-existe");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task Login_e_limitado_a_5_por_minuto_por_IP()
    {
        var cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add(ApiFactory.HeaderIpRemoto, "198.51.100.77");

        var status = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            status.Add((await cliente.PostAsJsonAsync("/api/v1/auth/login", new { })).StatusCode);
        }

        Assert.All(status.Take(5), s => Assert.Equal(HttpStatusCode.BadRequest, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, status[5]);

        // Outro IP tem o próprio balde.
        var outro = _fabrica.CreateClient();
        outro.DefaultRequestHeaders.Add(ApiFactory.HeaderIpRemoto, "198.51.100.78");
        Assert.Equal(HttpStatusCode.BadRequest, (await outro.PostAsJsonAsync("/api/v1/auth/login", new { })).StatusCode);
    }

    [Fact]
    public async Task Resposta_429_traz_Retry_After_e_ProblemDetails()
    {
        var cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add(ApiFactory.HeaderIpRemoto, "198.51.100.90");
        HttpResponseMessage? ultima = null;
        for (var i = 0; i < 6; i++)
        {
            ultima = await cliente.PostAsJsonAsync("/api/v1/auth/login", new { });
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, ultima!.StatusCode);
        Assert.True(ultima.Headers.Contains("Retry-After"));
        Assert.Equal("/erros/limite-de-requisicoes", (await ultima.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Refresh_e_logout_exigem_Origin_da_mesma_origem()
    {
        var cliente = _fabrica.CreateClient();

        var semOrigin = await cliente.PostAsync("/api/v1/auth/refresh", null);
        var outraOrigem = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        outraOrigem.Headers.Add("Origin", "https://evil.example");
        var deOutraOrigem = await cliente.SendAsync(outraOrigem);
        var crossSite = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        crossSite.Headers.Add("Origin", cliente.BaseAddress!.GetLeftPart(UriPartial.Authority));
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");

        Assert.Equal(HttpStatusCode.Forbidden, semOrigin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deOutraOrigem.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cliente.SendAsync(crossSite)).StatusCode);
    }

    [Fact]
    public async Task Refresh_da_mesma_origem_sem_cookie_responde_401_e_apaga_o_cookie()
    {
        var cliente = _fabrica.CreateClient();
        var requisicao = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        requisicao.Headers.Add("Origin", cliente.BaseAddress!.GetLeftPart(UriPartial.Authority));

        var resposta = await cliente.SendAsync(requisicao);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        var setCookie = Assert.Single(resposta.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Secure-wr_refresh=;", setCookie, StringComparison.Ordinal);
        Assert.Contains("path=/api/v1/auth", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_pela_porta_publica_responde_404(string rota)
    {
        var cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add(ApiFactory.HeaderPorta, "8080");

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync(rota)).StatusCode);
    }

    [Fact]
    public async Task Health_pelo_Host_8081_enviado_para_a_porta_8080_continua_404()
    {
        var cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add(ApiFactory.HeaderPorta, "8080");
        cliente.DefaultRequestHeaders.Host = "localhost:8081"; // cabeçalho é do cliente; a porta do socket é que vale.

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Health_live_pela_porta_interna_responde_200()
    {
        var cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add(ApiFactory.HeaderPorta, "8081");

        var resposta = await cliente.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("Healthy", (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_ready_pela_porta_interna_nao_e_limitado_nem_exige_login()
    {
        var cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add(ApiFactory.HeaderPorta, "8081");

        // SQL e MinIO não existem aqui: Unhealthy (503) com Redis Degraded. O ponto é a rota existir na :8081, sem 401/429.
        var resposta = await cliente.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);
        var checks = (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("checks");
        Assert.Equal("Unhealthy", checks.GetProperty("sql").GetString());
        Assert.Equal("Unhealthy", checks.GetProperty("minio").GetString());
        Assert.Equal("Degraded", checks.GetProperty("redis").GetString());
    }

    [Theory]
    [InlineData("__GERAR__")]
    [InlineData("")]
    [InlineData("curta")]
    public void Boot_com_Jwt_SigningKey_invalida_falha(string chave)
    {
        using var fabrica = new ApiFactory();
        fabrica.Sobrescritas["Jwt:SigningKey"] = chave;

        var erro = Assert.Throws<OptionsValidationException>(() => fabrica.CreateClient());

        Assert.Contains("Jwt:SigningKey", erro.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Seguranca:Pepper")]
    [InlineData("Redis:Password")]
    [InlineData("Storage:SecretKey")]
    public void Boot_com_qualquer_segredo_de_exemplo_falha(string chave)
    {
        using var fabrica = new ApiFactory();
        fabrica.Sobrescritas[chave] = "__GERAR__";

        Assert.Throws<OptionsValidationException>(() => fabrica.CreateClient());
    }

    [Fact]
    public void Boot_com_connection_string_de_exemplo_falha()
    {
        using var fabrica = new ApiFactory();
        fabrica.Sobrescritas["ConnectionStrings:RadioDb"] = "Server=x;Password=__GERAR__";

        Assert.ThrowsAny<Exception>(() => fabrica.CreateClient());
    }
}
