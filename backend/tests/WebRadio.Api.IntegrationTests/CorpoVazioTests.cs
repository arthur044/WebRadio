using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using WebRadio.Domain.Enums;
using Xunit;
using static WebRadio.Api.IntegrationTests.ApiFactoryExtensions;

namespace WebRadio.Api.IntegrationTests;

/// <summary>Os records de request são string não anulável: JSON sem campos chega como null e o validator precisa pegar (400, não 500).</summary>
public class CorpoVazioTests
{
    private static ApiFactory Nova() => new() { RemoteIp = System.Net.IPAddress.Parse("203.0.113.10") };

    private static HttpClient Cliente(ApiFactory f) => f.CreateClient(new() { BaseAddress = new Uri("http://localhost") });

    [Theory]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/v1/auth/registrar")]
    public async Task Json_vazio_nas_rotas_anonimas_e_400_com_errors(string rota)
    {
        var r = await Cliente(Nova()).PostAsJsonAsync(rota, new { });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("/erros/validacao", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Json_vazio_em_trocar_senha_e_400_e_nao_500()
    {
        var f = Nova();
        f.CriarUsuario("ana@example.com", Role.Locutor);
        var c = Cliente(f);
        var token = (await (await c.PostAsJsonAsync("/api/v1/auth/login", new { email = "ana@example.com", senha = Senha })).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/trocar-senha") { Content = JsonContent.Create(new { }) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var r = await c.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
