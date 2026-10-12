using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Infrastructure.Persistencia;
using Xunit;
using static WebRadio.Api.IntegrationTests.ApiFactoryExtensions;

namespace WebRadio.Api.IntegrationTests;

/// <summary>
/// 02 §3 e a claim <c>sv</c> no nível HTTP, com banco em memória. A revogação de refresh usa UPDATE condicional (não roda
/// no InMemory), então o serviço de refresh é um fake que registra as chamadas; o RowVersion também não existe em memória
/// (ETag vazio). Trava, RowVersion real e revogação real são provados contra SQL Server em WebRadio.Infrastructure.Tests.
/// </summary>
public class UsuariosEndpointsTests
{
    private sealed class RefreshFalso : IRefreshTokenService
    {
        public List<Guid> RevogacoesGerais { get; } = [];

        public Task<ResultadoRotacao> RotacionarAsync(string tokenBruto, byte[] ipHash, CancellationToken ct) => Task.FromResult(ResultadoRotacao.Invalido);

        public Task RevogarFamiliaAsync(string tokenBruto, CancellationToken ct) => Task.CompletedTask;

        public Task RevogarTodasDoUsuarioAsync(Guid usuarioId, string? manterFamiliaDoToken, CancellationToken ct)
        {
            RevogacoesGerais.Add(usuarioId);
            return Task.CompletedTask;
        }
    }

    private const string ETagVazio = "\"\"";
    private const string Rota = "/api/v1/usuarios";

    /// <summary><c>Host</c> é o servidor que atende <c>C</c>: os singletons dele (throttle, cache) não são os de <c>F.Services</c>.</summary>
    private sealed record Cena(ApiFactory F, HttpClient C, RefreshFalso Refresh, string TokenAdmin, Guid AdminId, IServiceProvider Host);

    private static async Task<Cena> NovaCena()
    {
        var f = new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.20") };
        var refresh = new RefreshFalso();
        var host = f.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddScoped<IRefreshTokenService>(_ => refresh)));
        var c = host.CreateClient(new() { BaseAddress = new Uri("http://localhost") });
        var adminId = f.CriarUsuario("admin@example.com", Role.Admin);
        return new Cena(f, c, refresh, await TokenDe(c, "admin@example.com"), adminId, host.Services);
    }

    private static async Task<string> TokenDe(HttpClient c, string email)
    {
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new { email, senha = Senha });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Enviar(HttpClient c, HttpMethod metodo, string url, string? token, object? corpo = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(metodo, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (corpo is not null) req.Content = JsonContent.Create(corpo);
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await c.SendAsync(req);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static int OutboxDe(ApiFactory f, string tipo)
    {
        using var escopo = f.Services.CreateScope();
        return escopo.ServiceProvider.GetRequiredService<RadioDbContext>().EventosOutbox.Count(e => e.Tipo == tipo);
    }

    // ---- RBAC ----

    [Theory]
    [InlineData("GET", Rota)]
    [InlineData("POST", Rota)]
    [InlineData("PATCH", Rota + "/7b0e7c2e-0000-4000-8000-000000000001")]
    [InlineData("POST", Rota + "/7b0e7c2e-0000-4000-8000-000000000001/desbloquear-login")]
    public async Task Sem_token_e_401_e_Locutor_ou_Ouvinte_e_403(string metodo, string url)
    {
        var cena = await NovaCena();
        cena.F.CriarUsuario("locutor@example.com", Role.Locutor);
        cena.F.CriarUsuario("ouvinte@example.com", Role.Ouvinte);
        var corpo = metodo == "GET" ? null : new { nome = "X", email = "x@example.com", senhaInicial = "uma-senha-inicial-123", role = "Ouvinte" };

        Assert.Equal(HttpStatusCode.Unauthorized, (await Enviar(cena.C, new HttpMethod(metodo), url, null, corpo, ETagVazio)).StatusCode);
        foreach (var email in new[] { "locutor@example.com", "ouvinte@example.com" })
            Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(cena.C, new HttpMethod(metodo), url, await TokenDe(cena.C, email), corpo, ETagVazio)).StatusCode);
    }

    [Fact]
    public async Task Admin_com_troca_de_senha_pendente_nao_gere_usuarios()
    {
        var cena = await NovaCena();
        cena.F.CriarUsuario("semeado@example.com", Role.Admin, deveTrocar: true);

        var r = await Enviar(cena.C, HttpMethod.Get, Rota, await TokenDe(cena.C, "semeado@example.com"));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("/erros/troca-de-senha-obrigatoria", await r.Content.ReadAsStringAsync());
    }

    // ---- GET ----

    [Fact]
    public async Task Lista_paginada_com_filtro_de_papel_e_busca_sem_expor_hash_e_sem_cache()
    {
        var cena = await NovaCena();
        cena.F.CriarUsuario("bia@example.com", Role.Locutor);
        cena.F.CriarUsuario("caio@example.com", Role.Locutor, ativo: false);
        cena.F.CriarUsuario("duda@example.com", Role.Ouvinte);

        var todos = await Enviar(cena.C, HttpMethod.Get, Rota, cena.TokenAdmin);
        Assert.Equal(HttpStatusCode.OK, todos.StatusCode);
        Assert.Equal("no-store", todos.Headers.CacheControl?.ToString());
        var j = await Json(todos);
        Assert.Equal(4, j.GetProperty("total").GetInt32());
        Assert.Equal(1, j.GetProperty("pagina").GetInt32());
        Assert.Equal(20, j.GetProperty("tamanho").GetInt32());
        var item = j.GetProperty("itens")[0];
        foreach (var campo in new[] { "id", "nome", "email", "role", "deveTrocarSenha", "ativo", "criadoEmUtc", "versao" })
            Assert.True(item.TryGetProperty(campo, out _), campo);
        Assert.DoesNotContain("senha", (await todos.Content.ReadAsStringAsync()).Replace("deveTrocarSenha", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

        var locutores = await Json(await Enviar(cena.C, HttpMethod.Get, Rota + "?role=Locutor", cena.TokenAdmin));
        Assert.Equal(2, locutores.GetProperty("total").GetInt32());
        Assert.Contains(locutores.GetProperty("itens").EnumerateArray(), u => !u.GetProperty("ativo").GetBoolean());

        var busca = await Json(await Enviar(cena.C, HttpMethod.Get, Rota + "?busca=DUDA@", cena.TokenAdmin));
        Assert.Equal("duda@example.com", Assert.Single(busca.GetProperty("itens").EnumerateArray()).GetProperty("email").GetString());

        var pagina2 = await Json(await Enviar(cena.C, HttpMethod.Get, Rota + "?pagina=2&tamanho=3", cena.TokenAdmin));
        Assert.Equal(4, pagina2.GetProperty("total").GetInt32());
        Assert.Single(pagina2.GetProperty("itens").EnumerateArray());
    }

    [Theory]
    [InlineData("?tamanho=101")]
    [InlineData("?tamanho=0")]
    [InlineData("?pagina=0")]
    [InlineData("?role=99")]
    [InlineData("?role=Chefe")]
    public async Task Paginacao_ou_papel_invalidos_sao_400(string query)
    {
        var cena = await NovaCena();
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(cena.C, HttpMethod.Get, Rota + query, cena.TokenAdmin)).StatusCode);
    }

    // ---- POST ----

    [Fact]
    public async Task Criar_devolve_201_com_ETag_e_a_conta_nasce_com_troca_de_senha_obrigatoria()
    {
        var cena = await NovaCena();

        var r = await Enviar(cena.C, HttpMethod.Post, Rota, cena.TokenAdmin,
            new { nome = "  Bia Locutora ", email = " bia@example.com ", senhaInicial = "uma-senha-inicial-123", role = "Locutor" });

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.NotNull(r.Headers.ETag);
        var j = await Json(r);
        Assert.Equal("Bia Locutora", j.GetProperty("nome").GetString());
        Assert.Equal("bia@example.com", j.GetProperty("email").GetString());
        Assert.Equal("Locutor", j.GetProperty("role").GetString());
        Assert.True(j.GetProperty("deveTrocarSenha").GetBoolean());
        Assert.True(j.GetProperty("ativo").GetBoolean());

        // A senha inicial funciona, mas o token nasce restrito.
        var login = await cena.C.PostAsJsonAsync("/api/v1/auth/login", new { email = "bia@example.com", senha = "uma-senha-inicial-123" });
        Assert.True((await Json(login)).GetProperty("deveTrocarSenha").GetBoolean());
    }

    [Fact]
    public async Task Criar_com_email_repetido_e_409_e_dados_invalidos_sao_400()
    {
        var cena = await NovaCena();
        object Corpo(string email = "novo@example.com", string senha = "uma-senha-inicial-123", string role = "Ouvinte", string nome = "Novo")
            => new { nome, email, senhaInicial = senha, role };

        var repetido = await Enviar(cena.C, HttpMethod.Post, Rota, cena.TokenAdmin, Corpo(email: "ADMIN@example.com"));
        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);
        Assert.Contains("/erros/conflito-cadastro", await repetido.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(cena.C, HttpMethod.Post, Rota, cena.TokenAdmin, Corpo(senha: "curta"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(cena.C, HttpMethod.Post, Rota, cena.TokenAdmin, Corpo(senha: "password1234"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(cena.C, HttpMethod.Post, Rota, cena.TokenAdmin, Corpo(email: "nao-e-email"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(cena.C, HttpMethod.Post, Rota, cena.TokenAdmin, Corpo(nome: " "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(cena.C, HttpMethod.Post, Rota, cena.TokenAdmin, Corpo(role: "Chefe"))).StatusCode);
    }

    // ---- PATCH ----

    [Fact]
    public async Task Alterar_sem_If_Match_e_428_com_If_Match_divergente_ou_malformado_e_412()
    {
        var cena = await NovaCena();
        var id = cena.F.CriarUsuario("bia@example.com", Role.Locutor);

        var sem = await Enviar(cena.C, HttpMethod.Patch, $"{Rota}/{id}", cena.TokenAdmin, new { role = "Admin" });
        Assert.Equal((HttpStatusCode)428, sem.StatusCode);
        Assert.Contains("/erros/precondicao-obrigatoria", await sem.Content.ReadAsStringAsync());

        foreach (var ifMatch in new[] { "\"AAAAAAAAB9E=\"", "W/\"\"", "*", "\"não é base64\"", "lixo" })
        {
            var r = await Enviar(cena.C, HttpMethod.Patch, $"{Rota}/{id}", cena.TokenAdmin, new { role = "Admin" }, ifMatch);
            Assert.Equal(HttpStatusCode.PreconditionFailed, r.StatusCode);
            Assert.Contains("/erros/versao-desatualizada", await r.Content.ReadAsStringAsync());
        }

        Assert.Empty(cena.Refresh.RevogacoesGerais);
    }

    [Fact]
    public async Task Mudar_o_papel_revoga_as_sessoes_enfileira_a_queda_do_hub_e_o_token_antigo_para_de_valer_na_hora()
    {
        var cena = await NovaCena();
        var id = cena.F.CriarUsuario("bia@example.com", Role.Locutor);
        var tokenDaBia = await TokenDe(cena.C, "bia@example.com");
        Assert.Equal(HttpStatusCode.OK, (await Enviar(cena.C, HttpMethod.Get, "/api/v1/auth/me", tokenDaBia)).StatusCode);

        var r = await Enviar(cena.C, HttpMethod.Patch, $"{Rota}/{id}", cena.TokenAdmin, new { role = "Ouvinte" }, ETagVazio);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("Ouvinte", (await Json(r)).GetProperty("role").GetString());
        Assert.NotNull(r.Headers.ETag);
        Assert.Equal([id], cena.Refresh.RevogacoesGerais);
        Assert.Equal(1, OutboxDe(cena.F, EventosDoOutbox.UsuarioSessoesRevogadas));

        // O access token emitido antes (sv antiga, role Locutor) cai sem esperar os 15 min do exp.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Enviar(cena.C, HttpMethod.Get, "/api/v1/auth/me", tokenDaBia)).StatusCode);
        // Um login novo recebe a sv atual e volta a funcionar; o token do Admin nunca foi afetado.
        Assert.Equal(HttpStatusCode.OK, (await Enviar(cena.C, HttpMethod.Get, "/api/v1/auth/me", await TokenDe(cena.C, "bia@example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Enviar(cena.C, HttpMethod.Get, Rota, cena.TokenAdmin)).StatusCode);
    }

    [Fact]
    public async Task Desativar_derruba_o_token_e_o_login_e_reativar_devolve_o_acesso()
    {
        var cena = await NovaCena();
        var id = cena.F.CriarUsuario("bia@example.com", Role.Locutor);
        var tokenDaBia = await TokenDe(cena.C, "bia@example.com");

        Assert.Equal(HttpStatusCode.OK, (await Enviar(cena.C, HttpMethod.Patch, $"{Rota}/{id}", cena.TokenAdmin, new { ativo = false }, ETagVazio)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Enviar(cena.C, HttpMethod.Get, "/api/v1/auth/me", tokenDaBia)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cena.C.PostAsJsonAsync("/api/v1/auth/login", new { email = "bia@example.com", senha = Senha })).StatusCode);

        var reativar = await Enviar(cena.C, HttpMethod.Patch, $"{Rota}/{id}", cena.TokenAdmin, new { ativo = true }, ETagVazio);
        Assert.True((await Json(reativar)).GetProperty("ativo").GetBoolean());
        // Reativar não revoga nada de novo nem ressuscita o token antigo.
        Assert.Single(cena.Refresh.RevogacoesGerais);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Enviar(cena.C, HttpMethod.Get, "/api/v1/auth/me", tokenDaBia)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Enviar(cena.C, HttpMethod.Get, "/api/v1/auth/me", await TokenDe(cena.C, "bia@example.com"))).StatusCode);
    }

    [Fact]
    public async Task Alteracao_que_nao_muda_nada_nao_revoga_sessoes_nem_derruba_o_token()
    {
        var cena = await NovaCena();
        var id = cena.F.CriarUsuario("bia@example.com", Role.Locutor);
        var tokenDaBia = await TokenDe(cena.C, "bia@example.com");

        Assert.Equal(HttpStatusCode.OK, (await Enviar(cena.C, HttpMethod.Patch, $"{Rota}/{id}", cena.TokenAdmin, new { role = "Locutor", ativo = true }, ETagVazio)).StatusCode);

        Assert.Empty(cena.Refresh.RevogacoesGerais);
        Assert.Equal(0, OutboxDe(cena.F, EventosDoOutbox.UsuarioSessoesRevogadas));
        Assert.Equal(HttpStatusCode.OK, (await Enviar(cena.C, HttpMethod.Get, "/api/v1/auth/me", tokenDaBia)).StatusCode);
    }

    [Theory]
    [InlineData("""{"role":"Locutor"}""")]
    [InlineData("""{"ativo":false}""")]
    public async Task Ultimo_Admin_ativo_nao_pode_ser_rebaixado_nem_desativado(string corpo)
    {
        var cena = await NovaCena();
        cena.F.CriarUsuario("ex-admin@example.com", Role.Admin, ativo: false); // inativo não conta

        var req = new HttpRequestMessage(HttpMethod.Patch, $"{Rota}/{cena.AdminId}") { Content = new StringContent(corpo, System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cena.TokenAdmin);
        req.Headers.TryAddWithoutValidation("If-Match", ETagVazio);
        var r = await cena.C.SendAsync(req);

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("/erros/transicao-invalida", await r.Content.ReadAsStringAsync());
        Assert.Empty(cena.Refresh.RevogacoesGerais);
        Assert.Equal(HttpStatusCode.OK, (await Enviar(cena.C, HttpMethod.Get, Rota, cena.TokenAdmin)).StatusCode);
    }

    [Fact]
    public async Task Com_dois_Admins_ativos_um_pode_rebaixar_o_outro()
    {
        var cena = await NovaCena();
        var outro = cena.F.CriarUsuario("outro-admin@example.com", Role.Admin);

        var r = await Enviar(cena.C, HttpMethod.Patch, $"{Rota}/{outro}", cena.TokenAdmin, new { role = "Locutor" }, ETagVazio);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Alterar_usuario_inexistente_e_404_e_corpo_vazio_e_400()
    {
        var cena = await NovaCena();

        var inexistente = await Enviar(cena.C, HttpMethod.Patch, $"{Rota}/{Guid.NewGuid()}", cena.TokenAdmin, new { ativo = false }, ETagVazio);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
        Assert.Contains("/erros/nao-encontrado", await inexistente.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(cena.C, HttpMethod.Patch, $"{Rota}/{cena.AdminId}", cena.TokenAdmin, new { }, ETagVazio)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Enviar(cena.C, HttpMethod.Patch, $"{Rota}/nao-e-guid", cena.TokenAdmin, new { ativo = false }, ETagVazio)).StatusCode);
    }

    // ---- trocar-senha e sv ----

    [Fact]
    public async Task Trocar_a_senha_invalida_o_access_token_em_uso()
    {
        var cena = await NovaCena();
        cena.F.CriarUsuario("bia@example.com", Role.Locutor);
        var token = await TokenDe(cena.C, "bia@example.com");

        var troca = await Enviar(cena.C, HttpMethod.Post, "/api/v1/auth/trocar-senha", token, new { senhaAtual = Senha, novaSenha = "outra-senha-bem-longa-456" });
        Assert.Equal(HttpStatusCode.NoContent, troca.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Enviar(cena.C, HttpMethod.Get, "/api/v1/auth/me", token)).StatusCode);
        var novo = await cena.C.PostAsJsonAsync("/api/v1/auth/login", new { email = "bia@example.com", senha = "outra-senha-bem-longa-456" });
        Assert.Equal(HttpStatusCode.OK, novo.StatusCode);
    }

    // ---- desbloquear-login ----

    [Fact]
    public async Task Desbloquear_login_libera_a_conta_bloqueada_e_usuario_inexistente_e_404()
    {
        var cena = await NovaCena();
        var id = cena.F.CriarUsuario("bia@example.com", Role.Locutor);
        // Outro IP erra 5 vezes: o par (conta, IP) fica bloqueado e nem a senha certa entra.
        cena.F.RemoteIp = IPAddress.Parse("203.0.113.99");
        for (var i = 0; i < 5; i++) await cena.C.PostAsJsonAsync("/api/v1/auth/login", new { email = "bia@example.com", senha = "senha-errada-000" });
        // O bloqueio é conferido direto no throttle do servidor: o limiter de 5/min por IP já responderia 401 de qualquer forma.
        using (var escopo = cena.Host.CreateScope())
        {
            var throttle = escopo.ServiceProvider.GetRequiredService<ILoginThrottle>();
            var ipHash = escopo.ServiceProvider.GetRequiredService<IIpHasher>().Hash(cena.F.RemoteIp);
            Assert.True((await throttle.AvaliarAsync("BIA@EXAMPLE.COM", ipHash, default)).Bloqueado);

            cena.F.RemoteIp = IPAddress.Parse("203.0.113.20");
            Assert.Equal(HttpStatusCode.NoContent, (await Enviar(cena.C, HttpMethod.Post, $"{Rota}/{id}/desbloquear-login", cena.TokenAdmin)).StatusCode);

            Assert.False((await throttle.AvaliarAsync("BIA@EXAMPLE.COM", ipHash, default)).Bloqueado);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await Enviar(cena.C, HttpMethod.Post, $"{Rota}/{Guid.NewGuid()}/desbloquear-login", cena.TokenAdmin)).StatusCode);
    }
}
