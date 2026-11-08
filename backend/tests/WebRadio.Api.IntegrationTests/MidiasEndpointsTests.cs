using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using Xunit;
using static WebRadio.Api.IntegrationTests.ApiFactoryExtensions;

namespace WebRadio.Api.IntegrationTests;

/// <summary>E4-F02 no nível HTTP (02 §6): <c>POST /midias/uploads</c> e <c>POST /midias/{id}/concluir</c>. O storage é falso (policy fixa, HEAD de um mapa).</summary>
public class MidiasEndpointsTests
{
    private const string Rota = "/api/v1/midias";

    private sealed class StorageFalso : IObjectStorage
    {
        public Dictionary<string, InfoObjeto> Objetos { get; } = [];

        public Task<PostPolicyAssinada> CriarPostPolicyAsync(string bucket, string chave, string contentType, long tamanhoMaximoBytes, TimeSpan validade, CancellationToken ct = default) =>
            Task.FromResult(new PostPolicyAssinada($"http://localhost:8080/storage/{bucket}", new Dictionary<string, string> { ["key"] = chave }, DateTime.UtcNow + validade));

        public Task<InfoObjeto?> HeadAsync(string bucket, string chave, CancellationToken ct = default) => Task.FromResult(Objetos.TryGetValue($"{bucket}/{chave}", out var o) ? o : null);

        public Task<string> UrlGetPreAssinadaAsync(string bucket, string chave, TimeSpan validade, string? contentType = null, string? contentDisposition = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task MoverAsync(string bucketOrigem, string chaveOrigem, string bucketDestino, string chaveDestino, string etagEsperado, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> VerificarAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task PutAsync(string bucket, string chave, Stream conteudo, long tamanhoBytes, string contentType, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(string bucket, string chave, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ObjetoListado>> ListarAsync(string bucket, string prefixo, CancellationToken ct = default) => throw new NotSupportedException();

        public string UrlPublica(string bucket, string chave) => throw new NotSupportedException();
    }

    private sealed record Cena(HttpClient C, StorageFalso Storage);

    private static Cena NovaCena()
    {
        var f = new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.50") };
        var storage = new StorageFalso();
        var host = f.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IObjectStorage>(storage)));
        f.CriarUsuario("admin@example.com", Role.Admin);
        f.CriarUsuario("locutor@example.com", Role.Locutor);
        f.CriarUsuario("locutor2@example.com", Role.Locutor);
        f.CriarUsuario("ouvinte@example.com", Role.Ouvinte);
        return new Cena(host.CreateClient(new() { BaseAddress = new Uri("http://localhost") }), storage);
    }

    private static async Task<string> TokenDe(HttpClient c, string email)
    {
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new { email, senha = Senha });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, string url, string? token, object? corpo = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (corpo is not null) req.Content = JsonContent.Create(corpo);
        return c.SendAsync(req);
    }

    private static object Corpo(string nome = "vinheta.mp3", string tipo = "Vinheta", long tamanho = 500_000, string mime = "audio/mpeg") =>
        new { nomeOriginal = nome, tipoMidia = tipo, tamanhoBytes = tamanho, mimeTypeDeclarado = mime };

    [Fact]
    public async Task Upload_e_do_PodeModerar_anonimo_401_e_ouvinte_403()
    {
        var c = NovaCena();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(c.C, Rota + "/uploads", null, Corpo())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(c.C, Rota + "/uploads", await TokenDe(c.C, "ouvinte@example.com"), Corpo())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(c.C, Rota + "/uploads", await TokenDe(c.C, "locutor@example.com"), Corpo())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(c.C, Rota + "/uploads", await TokenDe(c.C, "admin@example.com"), Corpo())).StatusCode);
    }

    [Fact]
    public async Task Upload_responde_201_com_midiaId_e_a_policy()
    {
        var c = NovaCena();
        var r = await Post(c.C, Rota + "/uploads", await TokenDe(c.C, "locutor@example.com"), Corpo());

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await Json(r);
        var id = j.GetProperty("midiaId").GetGuid();
        var upload = j.GetProperty("upload");
        Assert.Equal("http://localhost:8080/storage/quarentena", upload.GetProperty("url").GetString());
        Assert.EndsWith(id.ToString(), upload.GetProperty("campos").GetProperty("key").GetString());
        Assert.True(upload.TryGetProperty("expiraEmUtc", out _));
    }

    [Theory]
    [InlineData("a/b.mp3", "Vinheta", 100, "audio/mpeg")]
    [InlineData("a.mp3", "Vinheta", 100, "text/html")]
    [InlineData("a.mp3", "Vinheta", 10_485_761, "audio/mpeg")]
    [InlineData("a.mp3", "Musica", 100, "audio/mpeg")] // música sem título
    [InlineData("a.mp3", "Inexistente", 100, "audio/mpeg")]
    public async Task Corpo_invalido_e_400(string nome, string tipo, long tamanho, string mime)
    {
        var c = NovaCena();
        var r = await Post(c.C, Rota + "/uploads", await TokenDe(c.C, "locutor@example.com"), Corpo(nome, tipo, tamanho, mime));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Sem_tipo_ou_tamanho_e_400()
    {
        var c = NovaCena();
        var token = await TokenDe(c.C, "locutor@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c.C, Rota + "/uploads", token, new { nomeOriginal = "a.mp3", mimeTypeDeclarado = "audio/mpeg" })).StatusCode);
    }

    [Fact]
    public async Task O_sexto_aberto_e_429_com_retry_after()
    {
        var c = NovaCena();
        var token = await TokenDe(c.C, "locutor@example.com");
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Created, (await Post(c.C, Rota + "/uploads", token, Corpo())).StatusCode);

        var r = await Post(c.C, Rota + "/uploads", token, Corpo());
        Assert.Equal((HttpStatusCode)429, r.StatusCode);
        Assert.NotNull(r.Headers.RetryAfter);
        // Outro usuário tem a própria cota.
        Assert.Equal(HttpStatusCode.Created, (await Post(c.C, Rota + "/uploads", await TokenDe(c.C, "locutor2@example.com"), Corpo())).StatusCode);
    }

    [Fact]
    public async Task Concluir_com_o_objeto_no_storage_e_202_e_sem_ele_400()
    {
        var c = NovaCena();
        var token = await TokenDe(c.C, "locutor@example.com");
        var j = await Json(await Post(c.C, Rota + "/uploads", token, Corpo(tamanho: 500_000)));
        var id = j.GetProperty("midiaId").GetGuid();
        var chave = j.GetProperty("upload").GetProperty("campos").GetProperty("key").GetString()!;

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c.C, $"{Rota}/{id}/concluir", token)).StatusCode);

        c.Storage.Objetos[$"quarentena/{chave}"] = new InfoObjeto(500_000, "audio/mpeg", "\"abc\"");
        Assert.Equal(HttpStatusCode.Accepted, (await Post(c.C, $"{Rota}/{id}/concluir", token)).StatusCode);
    }

    [Fact]
    public async Task Concluir_de_outro_locutor_e_404_e_o_admin_pode()
    {
        var c = NovaCena();
        var dono = await TokenDe(c.C, "locutor@example.com");
        var j = await Json(await Post(c.C, Rota + "/uploads", dono, Corpo(tamanho: 1000)));
        var id = j.GetProperty("midiaId").GetGuid();
        c.Storage.Objetos[$"quarentena/{j.GetProperty("upload").GetProperty("campos").GetProperty("key").GetString()}"] = new InfoObjeto(1000, "audio/mpeg", "abc");

        Assert.Equal(HttpStatusCode.NotFound, (await Post(c.C, $"{Rota}/{id}/concluir", await TokenDe(c.C, "locutor2@example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(c.C, $"{Rota}/{id}/concluir", await TokenDe(c.C, "admin@example.com"))).StatusCode);
    }
}
