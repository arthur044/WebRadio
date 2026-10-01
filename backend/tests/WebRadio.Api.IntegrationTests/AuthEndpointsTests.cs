using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Api.Infra;
using WebRadio.Domain.Enums;
using Xunit;
using static WebRadio.Api.IntegrationTests.ApiFactoryExtensions;

namespace WebRadio.Api.IntegrationTests;

/// <summary>E1-F07a. Banco em memória e sem Docker: rodam no backend-unit.</summary>
public class AuthEndpointsTests
{
    private sealed class Rotas : IEndpointModule
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/v1/teste/moderar", () => "ok").RequireAuthorization("PodeModerar");
            app.MapGet("/api/v1/teste/admin", () => "ok").RequireAuthorization("SomenteAdmin");
            app.MapGet("/api/v1/teste/qualquer", () => "ok");
        }
    }

    private static HttpClient Cliente(ApiFactory f)
        => f.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IEndpointModule, Rotas>()))
            .CreateClient(new() { BaseAddress = new Uri("http://localhost") });

    private static Task<HttpResponseMessage> Login(HttpClient c, string email, string senha)
        => c.PostAsJsonAsync("/api/v1/auth/login", new { email, senha });

    private static async Task<string> TokenDe(HttpClient c, string email)
        => (await (await Login(c, email, Senha)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;

    private static async Task<HttpResponseMessage> Get(HttpClient c, string url, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await c.SendAsync(req);
    }

    private static ApiFactory NovaFactory(string ip = "203.0.113.10")
        => new() { RemoteIp = IPAddress.Parse(ip) };

    [Fact]
    public async Task Login_ok_devolve_token_usuario_e_cookie_de_refresh_endurecido()
    {
        var f = NovaFactory();
        f.CriarUsuario("ana@example.com", Role.Locutor);
        var r = await Login(Cliente(f), "ana@example.com", Senha);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(j.GetProperty("deveTrocarSenha").GetBoolean());
        Assert.Equal("Locutor", j.GetProperty("usuario").GetProperty("role").GetString());
        Assert.False(j.GetProperty("usuario").TryGetProperty("senhaHash", out _));

        var cookie = r.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("__Secure-wr_refresh=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/api/v1/auth", cookie);
    }

    [Fact]
    public async Task Todo_401_de_login_tem_o_mesmo_corpo()
    {
        var f = NovaFactory();
        f.CriarUsuario("ana@example.com", Role.Locutor);
        f.CriarUsuario("off@example.com", Role.Locutor, ativo: false);
        var c = Cliente(f);

        var respostas = new[]
        {
            await Login(c, "ana@example.com", "senha-errada-errada"),
            await Login(c, "naoexiste@example.com", Senha),
            await Login(c, "off@example.com", Senha),
        };
        var corpos = new List<string>();
        foreach (var r in respostas)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            corpos.Add(await r.Content.ReadAsStringAsync());
        }

        Assert.Single(corpos.Distinct());
        Assert.Contains("/erros/credenciais", corpos[0]);
        Assert.DoesNotContain("example.com", corpos[0]);
    }

    [Fact]
    public async Task Sexta_tentativa_do_mesmo_IP_e_401_identico_mesmo_com_senha_certa_e_outro_IP_ainda_loga()
    {
        var f = NovaFactory("203.0.113.20");
        f.CriarUsuario("ana@example.com", Role.Locutor);
        var c = Cliente(f);

        string? corpoDeFalha = null;
        for (var i = 0; i < 5; i++)
        {
            var r = await Login(c, "ana@example.com", "senha-errada-errada");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            corpoDeFalha = await r.Content.ReadAsStringAsync();
        }

        var sexta = await Login(c, "ana@example.com", Senha); // senha CERTA, 6ª no minuto
        Assert.Equal(HttpStatusCode.Unauthorized, sexta.StatusCode);
        Assert.Equal(corpoDeFalha, await sexta.Content.ReadAsStringAsync());
        Assert.False(sexta.Headers.Contains("Retry-After"));

        f.RemoteIp = IPAddress.Parse("198.51.100.77");
        Assert.Equal(HttpStatusCode.OK, (await Login(c, "ana@example.com", Senha)).StatusCode);
    }

    [Fact]
    public async Task Registrar_cria_Ouvinte_ignora_role_no_corpo_e_recusa_duplicado_e_senha_fraca()
    {
        var c = Cliente(NovaFactory());
        var ok = await c.PostAsJsonAsync("/api/v1/auth/registrar", new { nome = "Bia", email = "bia@example.com", senha = Senha, role = "Admin" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal("Ouvinte", (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());

        var dup = await c.PostAsJsonAsync("/api/v1/auth/registrar", new { nome = "Bia", email = "BIA@example.com", senha = Senha });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var fraca = await c.PostAsJsonAsync("/api/v1/auth/registrar", new { nome = "Cid", email = "cid@example.com", senha = "curta" });
        Assert.Equal(HttpStatusCode.BadRequest, fraca.StatusCode);
        Assert.Contains("Senha", await fraca.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Registrar_tem_limite_de_3_por_hora_por_IP()
    {
        var c = Cliente(NovaFactory("203.0.113.30"));
        HttpStatusCode ultimo = default;
        for (var i = 0; i < 4; i++)
            ultimo = (await c.PostAsJsonAsync("/api/v1/auth/registrar", new { nome = "N", email = $"n{i}@example.com", senha = Senha })).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, ultimo);
    }

    [Fact]
    public async Task Me_exige_token_e_devolve_o_proprio_usuario()
    {
        var f = NovaFactory();
        f.CriarUsuario("ana@example.com", Role.Locutor);
        var c = Cliente(f);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(c, "/api/v1/auth/me", null)).StatusCode);

        var me = await Get(c, "/api/v1/auth/me", await TokenDe(c, "ana@example.com"));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("ana@example.com", (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("email").GetString());
    }

    [Fact]
    public async Task Politicas_de_papel_Ouvinte_Locutor_Admin()
    {
        var f = NovaFactory();
        f.CriarUsuario("o@example.com", Role.Ouvinte);
        f.CriarUsuario("l@example.com", Role.Locutor);
        f.CriarUsuario("a@example.com", Role.Admin);
        var c = Cliente(f);
        var o = await TokenDe(c, "o@example.com");
        var l = await TokenDe(c, "l@example.com");
        var a = await TokenDe(c, "a@example.com");

        Assert.Equal(HttpStatusCode.Forbidden, (await Get(c, "/api/v1/teste/admin", o)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(c, "/api/v1/teste/moderar", o)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/v1/teste/moderar", l)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(c, "/api/v1/teste/admin", l)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/v1/teste/admin", a)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/v1/teste/moderar", a)).StatusCode);
    }

    [Fact]
    public async Task Token_com_role_adulterada_no_payload_e_401()
    {
        var f = NovaFactory();
        f.CriarUsuario("o@example.com", Role.Ouvinte);
        var c = Cliente(f);
        var partes = (await TokenDe(c, "o@example.com")).Split('.');
        var payload = System.Text.Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(partes[1])).Replace("\"Ouvinte\"", "\"Admin\"");
        var forjado = $"{partes[0]}.{System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(payload))}.{partes[2]}";

        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(c, "/api/v1/teste/admin", forjado)).StatusCode);
    }

    [Fact]
    public async Task Com_DeveTrocarSenha_so_me_e_trocar_senha_estao_liberados()
    {
        var f = NovaFactory();
        f.CriarUsuario("admin@example.com", Role.Admin, deveTrocar: true);
        var c = Cliente(f);
        var login = await Login(c, "admin@example.com", Senha);
        Assert.True((await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deveTrocarSenha").GetBoolean());
        var token = await TokenDe(c, "admin@example.com");

        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/v1/auth/me", token)).StatusCode);
        var bloqueada = await Get(c, "/api/v1/teste/admin", token);
        Assert.Equal(HttpStatusCode.Forbidden, bloqueada.StatusCode);
        Assert.Contains("/erros/troca-de-senha-obrigatoria", await bloqueada.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Chave_anterior_configurada_continua_validando_token_antigo_na_rotacao()
    {
        var antiga = ApiFactory.ChaveValida;
        var f1 = NovaFactory();
        f1.CriarUsuario("ana@example.com", Role.Locutor);
        var token = await TokenDe(Cliente(f1), "ana@example.com"); // emitido com a chave "atual" = antiga

        var f2 = NovaFactory();
        f2.Config["Jwt:SigningKey"] = System.Buffers.Text.Base64Url.EncodeToString(Enumerable.Range(0, 48).Select(i => (byte)(i * 5 + 3)).ToArray());
        f2.Config["Jwt:SigningKeyAnterior"] = antiga;
        f2.CriarUsuario("ana@example.com", Role.Locutor);
        Assert.Equal(HttpStatusCode.OK, (await Get(Cliente(f2), "/api/v1/teste/qualquer", token)).StatusCode);

        var f3 = NovaFactory();
        f3.Config["Jwt:SigningKey"] = f2.Config["Jwt:SigningKey"];
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(Cliente(f3), "/api/v1/teste/qualquer", token)).StatusCode);
    }
}
