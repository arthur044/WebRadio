using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using WebRadio.Domain.Enums;
using Xunit;
using static WebRadio.Api.IntegrationTests.ApiFactoryExtensions;

namespace WebRadio.Api.IntegrationTests;

/// <summary>E4-A02 (02 §11): <c>GET /relatorios/mais-tocadas</c> é do Admin; entrada fora do contrato é 400. A procedure em si é testada com SQL real.</summary>
public class RelatoriosEndpointsTests
{
    private const string Rota = "/api/v1/relatorios/mais-tocadas";

    private static (ApiFactory F, HttpClient C) Novo()
    {
        var f = new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.60") };
        f.CriarUsuario("admin@example.com", Role.Admin);
        f.CriarUsuario("locutor@example.com", Role.Locutor);
        return (f, f.CreateClient(new() { BaseAddress = new Uri("http://localhost") }));
    }

    private static async Task<string> TokenDe(HttpClient c, string email)
    {
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new { email, senha = Senha });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static Task<HttpResponseMessage> Get(HttpClient c, string url, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c.SendAsync(req);
    }

    [Fact]
    public async Task So_o_admin_le_o_relatorio_sem_token_e_401_e_locutor_e_403()
    {
        var (_, c) = Novo();
        var url = $"{Rota}?de=2026-11-01T00:00:00Z&ate=2026-11-08T00:00:00Z";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(c, url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(c, url, await TokenDe(c, "locutor@example.com"))).StatusCode);
    }

    [Fact]
    public async Task Admin_recebe_a_lista_com_os_campos_do_contrato_e_o_top_padrao_e_20()
    {
        var (_, c) = Novo();
        var token = await TokenDe(c, "admin@example.com");

        var r = await Get(c, $"{Rota}?de=2026-11-01T00:00:00Z&ate=2026-11-08T00:00:00Z", token);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var item = (await r.Content.ReadFromJsonAsync<JsonElement>())[0];
        Assert.Equal("Aurora", item.GetProperty("titulo").GetString());
        Assert.Equal("Musica", item.GetProperty("tipoMidia").GetString());
        Assert.Equal(20, item.GetProperty("reproducoes").GetInt32()); // o fake devolve o top pedido: o padrão
        Assert.True(item.TryGetProperty("midiaId", out _) && item.TryGetProperty("artista", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?de=2026-11-01T00:00:00Z")]
    [InlineData("?ate=2026-11-08T00:00:00Z")]
    [InlineData("?de=2026-11-08T00:00:00Z&ate=2026-11-01T00:00:00Z")]
    [InlineData("?de=2026-11-01T00:00:00Z&ate=2026-11-01T00:00:00Z")]
    [InlineData("?de=2025-01-01T00:00:00Z&ate=2026-11-08T00:00:00Z")]
    [InlineData("?de=2026-11-01T00:00:00Z&ate=2026-11-08T00:00:00Z&top=0")]
    [InlineData("?de=2026-11-01T00:00:00Z&ate=2026-11-08T00:00:00Z&top=101")]
    [InlineData("?de=ontem&ate=hoje")]
    public async Task Fora_do_contrato_e_400(string consulta)
    {
        var (_, c) = Novo();
        var r = await Get(c, Rota + consulta, await TokenDe(c, "admin@example.com"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
