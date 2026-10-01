using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WebRadio.Api.Infra;
using WebRadio.Domain.Erros;
using Xunit;

namespace WebRadio.Api.IntegrationTests;

/// <summary>E1-F06. Não precisam de Docker: sem [Categoria=Integracao], rodam no backend-unit.</summary>
public class HostTests
{
    private enum Cor { Vermelho }

    /// <summary>Rotas de teste entram como módulos de endpoint (mesmo mecanismo das features), sem poluir a Api.</summary>
    private static HttpClient Cliente(ApiFactory f, Action<IEndpointRouteBuilder>? rotas = null)
        => f.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IEndpointModule>(new RotasDeTeste(rotas ?? (_ => { })))))
            .CreateClient(new() { BaseAddress = new Uri("http://localhost") });

    private sealed class RotasDeTeste(Action<IEndpointRouteBuilder> rotas) : IEndpointModule
    {
        public void Map(IEndpointRouteBuilder app) => rotas(app);
    }

    [Fact]
    public async Task Rota_sem_token_cai_na_FallbackPolicy_e_devolve_401()
    {
        var r = await Cliente(new ApiFactory(), e => e.MapGet("/api/v1/segredo", () => "x")).GetAsync("/api/v1/segredo");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Rota_AllowAnonymous_e_acessivel_e_enum_sai_como_string()
    {
        var r = await Cliente(new ApiFactory(), e => e.MapGet("/api/v1/cor", () => new { cor = Cor.Vermelho }).AllowAnonymous()).GetAsync("/api/v1/cor");
        Assert.Equal("""{"cor":"Vermelho"}""", await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(typeof(ConflitoDeGradeException), 409, "/erros/conflito-grade")]
    [InlineData(typeof(TransicaoInvalidaException), 409, "/erros/transicao-invalida")]
    [InlineData(typeof(PedidoJaModeradoException), 409, "/erros/transicao-invalida")]
    public async Task Erro_de_dominio_vira_ProblemDetails(Type tipo, int status, string type)
    {
        Exception Criar() => tipo == typeof(ConflitoDeGradeException) ? new ConflitoDeGradeException(Guid.NewGuid())
            : tipo == typeof(PedidoJaModeradoException) ? new PedidoJaModeradoException(Guid.NewGuid())
            : new TransicaoInvalidaException("não pode");
        var r = await Cliente(new ApiFactory(), e => e.MapGet("/api/v1/erro", (Func<string>)(() => throw Criar())).AllowAnonymous()).GetAsync("/api/v1/erro");
        Assert.Equal(status, (int)r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(type, j.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Conflito_de_grade_inclui_programaConflitanteId()
    {
        var id = Guid.NewGuid();
        var r = await Cliente(new ApiFactory(), e => e.MapGet("/api/v1/c", (Func<string>)(() => throw new ConflitoDeGradeException(id))).AllowAnonymous()).GetAsync("/api/v1/c");
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, j.GetProperty("programaConflitanteId").GetGuid());
    }

    [Fact]
    public async Task Excecao_inesperada_vira_500_generico_sem_vazar_a_mensagem()
    {
        var r = await Cliente(new ApiFactory(), e => e.MapGet("/api/v1/boom", (Func<string>)(() => throw new InvalidOperationException("segredo-interno"))).AllowAnonymous()).GetAsync("/api/v1/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.DoesNotContain("segredo-interno", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ValidationException_vira_400_ProblemDetails_com_errors()
    {
        var r = await Cliente(new ApiFactory(), e => e.MapGet("/api/v1/v", (Func<string>)(() =>
            throw new FluentValidation.ValidationException([new FluentValidation.Results.ValidationFailure("Nome", "Nome é obrigatório.")]))).AllowAnonymous()).GetAsync("/api/v1/v");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/erros/validacao", j.GetProperty("type").GetString());
        Assert.Equal("Nome é obrigatório.", j.GetProperty("errors").GetProperty("Nome")[0].GetString());
    }

    [Fact]
    public async Task Host_fora_do_AllowedHosts_e_recusado()
    {
        var c = Cliente(new ApiFactory(), e => e.MapGet("/api/v1/x", () => "x").AllowAnonymous());
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/x");
        req.Headers.Host = "evil.example.com";
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Endpoint_interno_so_responde_na_porta_8081_do_socket_mesmo_com_Host_enganoso()
    {
        var c = Cliente(new ApiFactory(), e => e.MapGet("/interno", () => "ok").AllowAnonymous().RequireInternalListener());
        var pub = new HttpRequestMessage(HttpMethod.Get, "/interno");
        pub.Headers.Host = "localhost:8081";
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(pub)).StatusCode);

        var interno = new HttpRequestMessage(HttpMethod.Get, "/interno");
        interno.Headers.Add("X-Test-LocalPort", "8081");
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(interno)).StatusCode);
    }

    [Fact]
    public async Task XFF_forjado_de_fora_da_KnownNetworks_e_ignorado()
    {
        var f = new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.9") };
        var c = Cliente(f, e => e.MapGet("/ip", (HttpContext h) => h.Connection.RemoteIpAddress!.ToString()).AllowAnonymous());
        var req = new HttpRequestMessage(HttpMethod.Get, "/ip");
        req.Headers.Add("X-Forwarded-For", "10.9.9.9");
        Assert.Equal("203.0.113.9", await (await c.SendAsync(req)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task XFF_vindo_do_proxy_da_rede_app_e_respeitado()
    {
        var f = new ApiFactory { RemoteIp = IPAddress.Parse("172.28.0.5") };
        var c = Cliente(f, e => e.MapGet("/ip", (HttpContext h) => h.Connection.RemoteIpAddress!.ToString()).AllowAnonymous());
        var req = new HttpRequestMessage(HttpMethod.Get, "/ip");
        req.Headers.Add("X-Forwarded-For", "198.51.100.7");
        Assert.Equal("198.51.100.7", await (await c.SendAsync(req)).Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Jwt:SigningKey", "__GERAR__")]
    [InlineData("Jwt:SigningKey", "")]
    [InlineData("Seguranca:Pepper", "curto")]
    [InlineData("Playout:Token", "__GERAR__")]
    public void Boot_com_segredo_invalido_falha(string chave, string valor)
    {
        var f = new ApiFactory();
        f.Config[chave] = valor;
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }

    [Fact]
    public async Task OpenApi_so_em_Development_e_liberado_sem_token()
    {
        var r = await Cliente(new ApiFactory()).GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Health_live_so_na_porta_interna()
    {
        var c = Cliente(new ApiFactory());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/health/live")).StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        req.Headers.Add("X-Test-LocalPort", "8081");
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task RateLimiter_global_devolve_429_depois_de_100_por_minuto_do_mesmo_IP()
    {
        var f = new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.50") };
        var c = Cliente(f, e => e.MapGet("/rl", () => "x").AllowAnonymous());
        HttpStatusCode ultimo = default;
        for (var i = 0; i < 101; i++) ultimo = (await c.GetAsync("/rl")).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, ultimo);
    }

    [Fact]
    public async Task Endpoint_interno_sem_token_na_porta_publica_e_404_e_nao_401()
    {
        var c = Cliente(new ApiFactory(), e => e.MapPost("/api/v1/internal/x", () => "ok").RequireInternalListener());
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/v1/internal/x", null)).StatusCode);
    }

    [Fact]
    public async Task Listener_interno_e_isento_do_RateLimiter()
    {
        var c = Cliente(new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.60") }, e => e.MapGet("/rl", () => "x").AllowAnonymous());
        for (var i = 0; i < 120; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/rl");
            req.Headers.Add("X-Test-LocalPort", "8081");
            Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(req)).StatusCode);
        }
    }

    [Fact]
    public async Task Resposta_429_traz_Retry_After_e_corpo_ProblemDetails()
    {
        var c = Cliente(new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.70") }, e => e.MapGet("/rl", () => "x").AllowAnonymous());
        HttpResponseMessage r = null!;
        for (var i = 0; i < 101; i++) r = await c.GetAsync("/rl");
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.True(r.Headers.Contains("Retry-After"));
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void Producao_sem_SubnetApp_falha_no_boot()
    {
        var f = new ApiFactory { Ambiente = "Production" };
        f.Config.Remove("Rede:SubnetApp");
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }

    [Fact]
    public void Producao_com_AllowedHosts_curinga_falha_no_boot()
    {
        var f = new ApiFactory { Ambiente = "Production" };
        f.Config["AllowedHosts"] = "*";
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }

    [Fact]
    public void Producao_com_rede_e_hosts_explicitos_sobe()
        => new ApiFactory { Ambiente = "Production" }.CreateClient().Dispose();

    // ---- JWT (parâmetros do 05 §6) ----

    private static string Token(string chaveBase64Url, string iss = "webradio-api", string aud = "webradio", DateTime? exp = null, string alg = SecurityAlgorithms.HmacSha256)
    {
        var chave = new SymmetricSecurityKey(System.Buffers.Text.Base64Url.DecodeFromChars(chaveBase64Url));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = iss,
            Audience = aud,
            Expires = exp ?? DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = "u1", ["role"] = "Locutor" },
            SigningCredentials = new SigningCredentials(chave, alg),
        });
    }

    private static async Task<HttpStatusCode> ComToken(string token)
    {
        var c = Cliente(new ApiFactory(), e => e.MapGet("/api/v1/protegido", () => "ok"));
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/protegido");
        req.Headers.Authorization = new("Bearer", token);
        return (await c.SendAsync(req)).StatusCode;
    }

    [Fact]
    public async Task Jwt_valido_acessa_rota_protegida()
        => Assert.Equal(HttpStatusCode.OK, await ComToken(Token(ApiFactory.ChaveValida)));

    [Fact]
    public async Task Jwt_com_outra_chave_audience_errada_ou_expirado_e_401()
    {
        var outra = System.Buffers.Text.Base64Url.EncodeToString(Enumerable.Range(0, 48).Select(i => (byte)(i * 3 + 1)).ToArray());
        Assert.Equal(HttpStatusCode.Unauthorized, await ComToken(Token(outra)));
        Assert.Equal(HttpStatusCode.Unauthorized, await ComToken(Token(ApiFactory.ChaveValida, aud: "outra")));
        Assert.Equal(HttpStatusCode.Unauthorized, await ComToken(Token(ApiFactory.ChaveValida, iss: "outro")));
        Assert.Equal(HttpStatusCode.Unauthorized, await ComToken(Token(ApiFactory.ChaveValida, exp: DateTime.UtcNow.AddMinutes(-5))));
    }

    [Fact]
    public async Task Jwt_alg_none_e_401()
    {
        static string B64(string j) => System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(j));
        var exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var token = $"{B64("""{"alg":"none","typ":"JWT"}""")}.{B64($$"""{"sub":"u1","role":"Admin","iss":"webradio-api","aud":"webradio","exp":{{exp}}}""")}.";
        Assert.Equal(HttpStatusCode.Unauthorized, await ComToken(token));
    }
}
