using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Usuarios;
using Xunit;
using static WebRadio.Api.IntegrationTests.ApiFactoryExtensions;

namespace WebRadio.Api.IntegrationTests;

/// <summary>E2-F04 no nível HTTP (02 §4, credencial de transmissão). A corrida e a revogação por evento contra SQL Server de verdade estão em WebRadio.Infrastructure.Tests.</summary>
[Collection(ColecaoDeLogs.Nome)]
public class CredencialTransmissaoEndpointsTests
{
    private const string Minha = "/api/v1/locutores/me/credencial-transmissao";

    private sealed class Captura : ILogEventSink
    {
        private readonly List<LogEvent> _eventos = [];
        public void Emit(LogEvent e) { lock (_eventos) _eventos.Add(e); }

        /// <summary>Mensagem renderizada + todas as propriedades + exceção: tudo que iria para o console.</summary>
        public string Tudo()
        {
            lock (_eventos)
                return string.Join("\n", _eventos.Select(e => e.RenderMessage() + " " + string.Join(" ", e.Properties.Select(p => p.Value.ToString())) + " " + e.Exception));
        }
    }

    /// <summary>Usuários são criados pelo host DERIVADO: subir o host base (<c>F.Services</c>) trocaria o logger estático.</summary>
    private sealed record Cena(WebApplicationFactory<Program> Host, HttpClient C, Captura Log)
    {
        public Guid CriarUsuario(string email, Role role, bool deveTrocar = false)
        {
            using var escopo = Host.Services.CreateScope();
            var db = escopo.ServiceProvider.GetRequiredService<IRadioDbContext>();
            var hasher = escopo.ServiceProvider.GetRequiredService<ISenhaHasher>();
            var u = new Usuario(Guid.NewGuid(), "Fulano", email, hasher.Hash(Senha), role, deveTrocar, DateTime.UtcNow);
            db.Usuarios.Add(u);
            db.SaveChangesAsync().GetAwaiter().GetResult();
            return u.Id;
        }
    }

    private static Cena NovaCena()
    {
        var f = new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.30") };
        var log = new Captura();
        var host = f.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ILogEventSink>(log)));
        return new Cena(host, host.CreateClient(new() { BaseAddress = new Uri("http://localhost") }), log);
    }

    private static async Task<string> TokenDe(HttpClient c, string email)
    {
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new { email, senha = Senha });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static Task<HttpResponseMessage> Enviar(HttpClient c, HttpMethod metodo, string url, string? token, object? corpo = null)
    {
        var req = new HttpRequestMessage(metodo, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (corpo is not null) req.Content = JsonContent.Create(corpo);
        return c.SendAsync(req);
    }

    private static async Task<(Cena Cena, string Token, Guid Id)> LocutorAsync(Role role = Role.Locutor)
    {
        var cena = NovaCena();
        var id = cena.CriarUsuario("locutor@example.com", role);
        return (cena, await TokenDe(cena.C, "locutor@example.com"), id);
    }

    [Fact]
    public async Task Post_gera_201_no_store_e_o_segredo_so_aparece_nessa_resposta_e_em_nenhum_log()
    {
        var (cena, token, _) = await LocutorAsync();

        var r = await Enviar(cena.C, HttpMethod.Post, Minha, token, new { senhaAtual = Senha });

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        var corpo = await r.Content.ReadFromJsonAsync<JsonElement>();
        var segredo = corpo.GetProperty("segredo").GetString()!;
        var identificador = corpo.GetProperty("identificador").GetString()!;
        Assert.Matches("^[A-Za-z0-9_-]{43}$", segredo);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[0-9a-f]{4}-[0-9a-f]{12}$", identificador);
        Assert.True(corpo.TryGetProperty("criadaEmUtc", out _));

        var get = await Enviar(cena.C, HttpMethod.Get, Minha, token);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("no-store", get.Headers.CacheControl?.ToString());
        var texto = await get.Content.ReadAsStringAsync();
        Assert.Contains(identificador, texto);
        Assert.DoesNotContain(segredo, texto);
        Assert.DoesNotContain("segredo", texto, StringComparison.OrdinalIgnoreCase);

        var logs = cena.Log.Tudo();
        Assert.Contains("CREDENCIAL_TRANSMISSAO_GERADA", logs);
        Assert.DoesNotContain(segredo, logs);
        Assert.DoesNotContain(Senha, logs);
    }

    [Fact]
    public async Task Get_sem_credencial_e_204()
    {
        var (cena, token, _) = await LocutorAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await Enviar(cena.C, HttpMethod.Get, Minha, token)).StatusCode);
    }

    [Fact]
    public async Task Senha_atual_errada_e_401_e_a_sexta_tentativa_da_hora_e_429_com_Retry_After()
    {
        var (cena, token, _) = await LocutorAsync();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Enviar(cena.C, HttpMethod.Post, Minha, token, new { senhaAtual = "senha-errada-bem-longa" })).StatusCode);

        var sexta = await Enviar(cena.C, HttpMethod.Post, Minha, token, new { senhaAtual = Senha });
        Assert.Equal(HttpStatusCode.TooManyRequests, sexta.StatusCode);
        Assert.True(int.Parse(sexta.Headers.GetValues("Retry-After").Single()) > 0);
        var problema = await sexta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/erros/limite-excedido", problema.GetProperty("type").GetString());
        Assert.True(problema.GetProperty("retryAfterSeconds").GetInt32() > 0);
    }

    [Fact]
    public async Task Token_com_troca_de_senha_pendente_e_403()
    {
        var cena = NovaCena();
        cena.CriarUsuario("novo@example.com", Role.Locutor, deveTrocar: true);
        var token = await TokenDe(cena.C, "novo@example.com");

        var r = await Enviar(cena.C, HttpMethod.Post, Minha, token, new { senhaAtual = Senha });

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("troca-de-senha-obrigatoria", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ouvinte_e_403_e_anonimo_e_401()
    {
        var (cena, token, _) = await LocutorAsync(Role.Ouvinte);

        Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(cena.C, HttpMethod.Post, Minha, token, new { senhaAtual = Senha })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(cena.C, HttpMethod.Get, Minha, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Enviar(cena.C, HttpMethod.Get, Minha, null)).StatusCode);
    }

    [Fact]
    public async Task Delete_revoga_a_propria()
    {
        var (cena, token, _) = await LocutorAsync();
        await Enviar(cena.C, HttpMethod.Post, Minha, token, new { senhaAtual = Senha });

        Assert.Equal(HttpStatusCode.NoContent, (await Enviar(cena.C, HttpMethod.Delete, Minha, token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Enviar(cena.C, HttpMethod.Get, Minha, token)).StatusCode);
        Assert.Contains("CREDENCIAL_TRANSMISSAO_REVOGADA", cena.Log.Tudo());
    }

    [Fact]
    public async Task Admin_lista_as_ativas_e_revoga_a_de_outro_usuario()
    {
        var (cena, tokenLocutor, locutorId) = await LocutorAsync();
        var gerada = await (await Enviar(cena.C, HttpMethod.Post, Minha, tokenLocutor, new { senhaAtual = Senha })).Content.ReadFromJsonAsync<JsonElement>();
        cena.CriarUsuario("admin@example.com", Role.Admin);
        var admin = await TokenDe(cena.C, "admin@example.com");

        var lista = await Enviar(cena.C, HttpMethod.Get, "/api/v1/credenciais-transmissao", admin);
        Assert.Equal(HttpStatusCode.OK, lista.StatusCode);
        var item = Assert.Single((await lista.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(locutorId, item.GetProperty("usuarioId").GetGuid());
        Assert.Equal(gerada.GetProperty("identificador").GetString(), item.GetProperty("identificador").GetString());
        Assert.Equal("Fulano", item.GetProperty("usuarioNome").GetString());
        Assert.False(item.TryGetProperty("segredo", out _));

        Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(cena.C, HttpMethod.Get, "/api/v1/credenciais-transmissao", tokenLocutor)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Enviar(cena.C, HttpMethod.Delete, $"/api/v1/usuarios/{locutorId}/credencial-transmissao", tokenLocutor)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await Enviar(cena.C, HttpMethod.Delete, $"/api/v1/usuarios/{locutorId}/credencial-transmissao", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Enviar(cena.C, HttpMethod.Get, Minha, tokenLocutor)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Enviar(cena.C, HttpMethod.Delete, $"/api/v1/usuarios/{Guid.NewGuid()}/credencial-transmissao", admin)).StatusCode);
    }
}
