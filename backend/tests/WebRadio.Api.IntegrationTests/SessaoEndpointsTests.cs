using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using Xunit;
using static WebRadio.Api.IntegrationTests.ApiFactoryExtensions;

namespace WebRadio.Api.IntegrationTests;

/// <summary>
/// E1-F07b: refresh, logout e trocar-senha no nível HTTP. O UPDATE condicional do refresh não roda no InMemory, então o
/// serviço de refresh é um fake aqui (a rotação em si é testada contra SQL Server em WebRadio.Infrastructure.Tests).
/// </summary>
public class SessaoEndpointsTests
{
    private sealed class RefreshFalso : IRefreshTokenService
    {
        public ResultadoRotacao Proximo { get; set; } = ResultadoRotacao.Invalido;
        public List<string> Rotacionados { get; } = [];
        public List<string> FamiliasRevogadas { get; } = [];
        public List<(Guid Usuario, string? Manter)> RevogacoesGerais { get; } = [];

        public Task<ResultadoRotacao> RotacionarAsync(string tokenBruto, byte[] ipHash, CancellationToken ct)
        {
            Rotacionados.Add(tokenBruto);
            return Task.FromResult(Proximo);
        }

        public Task RevogarFamiliaAsync(string tokenBruto, CancellationToken ct) { FamiliaRevogadas(tokenBruto); return Task.CompletedTask; }

        public Task RevogarTodasDoUsuarioAsync(Guid usuarioId, string? manter, CancellationToken ct) { RevogacoesGerais.Add((usuarioId, manter)); return Task.CompletedTask; }

        private void FamiliaRevogadas(string t) => FamiliasRevogadas.Add(t);
    }

    private static string TokenValido() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpClient Cliente(ApiFactory f, RefreshFalso refresh)
        => f.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddScoped<IRefreshTokenService>(_ => refresh)))
            .CreateClient(new() { BaseAddress = new Uri("http://localhost"), HandleCookies = false });

    private static HttpRequestMessage Post(string url, string? origem = "http://localhost", string? cookie = null, string? fetchSite = null, string? bearer = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (origem is not null) req.Headers.Add("Origin", origem);
        if (cookie is not null) req.Headers.Add("Cookie", $"__Secure-wr_refresh={cookie}");
        if (fetchSite is not null) req.Headers.Add("Sec-Fetch-Site", fetchSite);
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    private static ApiFactory Nova() => new() { RemoteIp = IPAddress.Parse("203.0.113.10") };

    // ---- Origin (CSRF) ----

    [Theory]
    [InlineData("/api/v1/auth/refresh")]
    [InlineData("/api/v1/auth/logout")]
    public async Task Sem_Origin_ou_de_outra_origem_ou_cross_site_e_403(string rota)
    {
        var c = Cliente(Nova(), new RefreshFalso());

        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Post(rota, origem: null, cookie: TokenValido()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Post(rota, origem: "https://evil.example", cookie: TokenValido()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Post(rota, cookie: TokenValido(), fetchSite: "cross-site"))).StatusCode);
    }

    [Fact]
    public async Task Origem_extra_da_allowlist_vale_com_same_site_mas_nunca_com_cross_site()
    {
        var f = Nova();
        f.Config["Seguranca:OrigensPermitidas:0"] = "https://app.example.com";
        var refresh = new RefreshFalso();
        var c = Cliente(f, refresh);

        var mesmoSite = await c.SendAsync(Post("/api/v1/auth/refresh", origem: "https://app.example.com", cookie: TokenValido(), fetchSite: "same-site"));
        var crossSite = await c.SendAsync(Post("/api/v1/auth/refresh", origem: "https://app.example.com", cookie: TokenValido(), fetchSite: "cross-site"));

        Assert.Equal(HttpStatusCode.Unauthorized, mesmoSite.StatusCode); // passou pela checagem; o fake devolve inválido
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
    }

    [Fact]
    public async Task Atras_do_nginx_o_host_publico_com_porta_vem_do_X_Forwarded_Host_so_da_rede_app()
    {
        // O nginx do Prism manda X-Forwarded-Host = PUBLIC_AUTHORITY COM porta (ex.: localhost:8080).
        HttpRequestMessage Req() => Post("/api/v1/auth/refresh", origem: "http://localhost:8080", cookie: TokenValido());

        var daRedeApp = Cliente(new ApiFactory { RemoteIp = IPAddress.Parse("172.28.0.5") }, new RefreshFalso());
        var req = Req();
        req.Headers.Add("X-Forwarded-Host", "localhost:8080");
        req.Headers.Add("X-Forwarded-Proto", "http");
        Assert.Equal(HttpStatusCode.Unauthorized, (await daRedeApp.SendAsync(req)).StatusCode); // origem aceita

        // Quem NÃO está na rede app não consegue forjar o host da própria origem: o cabeçalho é ignorado → 403.
        var deFora = Cliente(Nova(), new RefreshFalso());
        var forjada = Req();
        forjada.Headers.Add("X-Forwarded-Host", "localhost:8080");
        forjada.Headers.Add("X-Forwarded-Proto", "http");
        Assert.Equal(HttpStatusCode.Forbidden, (await deFora.SendAsync(forjada)).StatusCode);
    }

    [Fact]
    public async Task AllowedHosts_lista_so_o_nome_e_aceita_o_host_com_qualquer_porta()
    {
        // O HostFiltering compara só o NOME do host (a porta do cabeçalho é ignorada). Uma entrada COM porta nunca casa.
        var f = Nova();
        f.Config["AllowedHosts"] = "localhost;api";
        var c = Cliente(f, new RefreshFalso());

        HttpRequestMessage Com(string host)
        {
            var r = Post("/api/v1/auth/refresh", origem: $"http://{host}", cookie: TokenValido());
            r.Headers.Host = host;
            return r;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Com("localhost:8080"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Com("api:8080"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Com("evil.example"))).StatusCode);
    }

    [Fact]
    public async Task AllowedHosts_com_porta_nunca_casa_e_por_isso_o_boot_de_producao_recusa()
    {
        var f = Nova();
        f.Config["AllowedHosts"] = "localhost:8080";
        var req = Post("/api/v1/auth/refresh", origem: "http://localhost:8080", cookie: TokenValido());
        req.Headers.Host = "localhost:8080";
        Assert.Equal(HttpStatusCode.BadRequest, (await Cliente(f, new RefreshFalso()).SendAsync(req)).StatusCode); // comprova o motivo

        var producao = new ApiFactory { Ambiente = "Production" };
        producao.Config["Redis:Host"] = "redis-inexistente";
        producao.Config["Redis:Password"] = new string('r', 32);
        producao.Config["AllowedHosts"] = "localhost:8080;api";
        Assert.ThrowsAny<Exception>(() => producao.CreateClient());
    }

    // ---- Refresh ----

    [Fact]
    public async Task Refresh_sem_cookie_ou_com_cookie_mal_formado_e_401_e_apaga_o_cookie()
    {
        var refresh = new RefreshFalso();
        var c = Cliente(Nova(), refresh);

        var semCookie = await c.SendAsync(Post("/api/v1/auth/refresh"));
        var lixo = await c.SendAsync(Post("/api/v1/auth/refresh", cookie: "lixo"));

        foreach (var r in new[] { semCookie, lixo })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            var setCookie = Assert.Single(r.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
            Assert.StartsWith("__secure-wr_refresh=;", setCookie, StringComparison.Ordinal);
            Assert.Contains("path=/api/v1/auth", setCookie);
            Assert.Equal("/erros/credenciais", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Refresh_valido_devolve_access_token_e_novo_cookie_endurecido_com_a_duracao_do_refresh()
    {
        var f = Nova();
        f.CriarUsuario("ana@example.com", Role.Locutor);
        Guid id;
        using (var escopo = f.Services.CreateScope())
            id = escopo.ServiceProvider.GetRequiredService<IRadioDbContext>().Usuarios.Single().Id;
        var novo = RefreshOpaco.De(RandomNumberGenerator.GetBytes(32));
        var refresh = new RefreshFalso { Proximo = new ResultadoRotacao(true, id, novo) };
        var c = Cliente(f, refresh);
        var antigo = TokenValido();

        var r = await c.SendAsync(Post("/api/v1/auth/refresh", cookie: antigo));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal([antigo], refresh.Rotacionados);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(j.GetProperty("accessToken").GetString()));
        Assert.Equal("ana@example.com", j.GetProperty("usuario").GetProperty("email").GetString());
        Assert.DoesNotContain(novo.Token, await r.Content.ReadAsStringAsync()); // o refresh NUNCA vai no corpo

        var cookie = r.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        Assert.StartsWith($"__secure-wr_refresh={novo.Token.ToLowerInvariant()};", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/api/v1/auth", cookie);
        Assert.Contains("max-age=604800", cookie);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Refresh_que_perde_a_corrida_e_401_com_tipo_proprio_e_SEM_Set_Cookie()
    {
        var c = Cliente(Nova(), new RefreshFalso { Proximo = ResultadoRotacao.PerdeuCorrida });

        var r = await c.SendAsync(Post("/api/v1/auth/refresh", cookie: TokenValido()));

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.False(r.Headers.Contains("Set-Cookie")); // apagar sobrescreveria o cookie novo gravado pela outra aba
        Assert.Equal("0", r.Headers.GetValues("Retry-After").Single());
        Assert.Equal("/erros/refresh-concorrente", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Refresh_nao_aceita_Bearer_no_lugar_do_cookie()
    {
        var f = Nova();
        f.CriarUsuario("ana@example.com", Role.Locutor);
        var c = Cliente(f, new RefreshFalso());
        var token = (await (await c.PostAsJsonAsync("/api/v1/auth/login", new { email = "ana@example.com", senha = Senha })).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;

        var r = await c.SendAsync(Post("/api/v1/auth/refresh", bearer: token));

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    // ---- Logout ----

    [Fact]
    public async Task Logout_revoga_a_familia_do_cookie_e_apaga_o_cookie_com_204()
    {
        var refresh = new RefreshFalso();
        var c = Cliente(Nova(), refresh);
        var token = TokenValido();

        var r = await c.SendAsync(Post("/api/v1/auth/logout", cookie: token));

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal([token], refresh.FamiliasRevogadas);
        Assert.StartsWith("__secure-wr_refresh=;", r.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_sem_cookie_tambem_e_204_e_nao_revoga_nada()
    {
        var refresh = new RefreshFalso();
        var r = await Cliente(Nova(), refresh).SendAsync(Post("/api/v1/auth/logout"));

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Empty(refresh.FamiliasRevogadas);
    }

    // ---- Trocar senha ----

    private static async Task<string> TokenDe(HttpClient c, string email, string senha = Senha)
        => (await (await c.PostAsJsonAsync("/api/v1/auth/login", new { email, senha })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;

    [Fact]
    public async Task Trocar_senha_exige_autenticacao()
    {
        var r = await Cliente(Nova(), new RefreshFalso()).PostAsJsonAsync("/api/v1/auth/trocar-senha", new { senhaAtual = "x", novaSenha = "y" });

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Trocar_senha_com_token_restrito_funciona_revoga_as_outras_sessoes_e_a_senha_antiga_para_de_valer()
    {
        var f = Nova();
        f.CriarUsuario("novo@example.com", Role.Admin, deveTrocar: true);
        var refresh = new RefreshFalso();
        var c = Cliente(f, refresh);
        var token = await TokenDe(c, "novo@example.com");
        var cookieAtual = TokenValido();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/trocar-senha")
        {
            Content = JsonContent.Create(new { senhaAtual = Senha, novaSenha = "uma-senha-nova-forte-9" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Cookie", $"__Secure-wr_refresh={cookieAtual}");
        var r = await c.SendAsync(req);

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var (_, manter) = Assert.Single(refresh.RevogacoesGerais);
        Assert.Equal(cookieAtual, manter); // a sessão atual sobrevive; as outras caem (S-M11)
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/auth/login", new { email = "novo@example.com", senha = Senha })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/auth/login", new { email = "novo@example.com", senha = "uma-senha-nova-forte-9" })).StatusCode);
    }

    [Theory]
    [InlineData("curta")]
    [InlineData("password1234")]
    [InlineData("senha-atual-aqui")] // igual à atual (substituída no teste)
    public async Task Trocar_senha_valida_a_nova_senha(string nova)
    {
        var f = Nova();
        f.CriarUsuario("ana@example.com", Role.Locutor);
        var c = Cliente(f, new RefreshFalso());
        var token = await TokenDe(c, "ana@example.com");

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/trocar-senha")
        {
            Content = JsonContent.Create(new { senhaAtual = Senha, novaSenha = nova == "senha-atual-aqui" ? Senha : nova }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var r = await c.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.True((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("NovaSenha", out _));
    }

    [Fact]
    public async Task Trocar_senha_com_senha_atual_errada_e_400_e_nao_revoga_sessao()
    {
        var f = Nova();
        f.CriarUsuario("ana@example.com", Role.Locutor);
        var refresh = new RefreshFalso();
        var c = Cliente(f, refresh);
        var token = await TokenDe(c, "ana@example.com");

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/trocar-senha")
        {
            Content = JsonContent.Create(new { senhaAtual = "senha-errada-1234", novaSenha = "uma-senha-nova-forte-9" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var r = await c.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(refresh.RevogacoesGerais);
    }
}
