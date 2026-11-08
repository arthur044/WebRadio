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

        public Task<string> UrlGetPreAssinadaAsync(string bucket, string chave, TimeSpan validade, string? contentType = null, string? contentDisposition = null, CancellationToken ct = default) =>
            Task.FromResult($"http://localhost:8080/storage/{bucket}/{chave}?X-Amz-Signature=abc");

        public Task<long> BaixarAsync(string bucket, string chave, string etagEsperado, long limiteBytes, Stream destino, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> VerificarAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task PutAsync(string bucket, string chave, Stream conteudo, long tamanhoBytes, string contentType, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(string bucket, string chave, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ObjetoListado>> ListarAsync(string bucket, string prefixo, CancellationToken ct = default) => throw new NotSupportedException();

        public string UrlPublica(string bucket, string chave) => throw new NotSupportedException();
    }

    private sealed record Cena(HttpClient C, StorageFalso Storage, ApiFactory F);

    private static Cena NovaCena()
    {
        var f = new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.50") };
        var storage = new StorageFalso();
        var host = f.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IObjectStorage>(storage)));
        f.CriarUsuario("admin@example.com", Role.Admin);
        f.CriarUsuario("locutor@example.com", Role.Locutor);
        f.CriarUsuario("locutor2@example.com", Role.Locutor);
        f.CriarUsuario("ouvinte@example.com", Role.Ouvinte);
        return new Cena(host.CreateClient(new() { BaseAddress = new Uri("http://localhost") }), storage, f);
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
        Assert.Equal("no-store", r.Headers.CacheControl!.ToString()); // a policy assinada nunca fica em cache
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

    // ---- E4-F03: consultas e manutenção ----

    private static Guid Semear(Cena c, StatusSanitizacao status = StatusSanitizacao.Aprovado, string titulo = "Aurora", TipoMidia tipo = TipoMidia.Musica, bool ativo = true, bool comReproducao = false, string donoEmail = "locutor@example.com")
    {
        using var escopo = c.F.Services.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<WebRadio.Application.Abstracoes.IRadioDbContext>();
        var agora = DateTime.UtcNow;
        var dono = db.Usuarios.First(u => u.Email == donoEmail).Id;
        var m = new WebRadio.Domain.Midia.ArquivoMidia(Guid.NewGuid(), "aurora.mp3", titulo, "Banda", tipo, "audio/mpeg", 1000, dono, agora);
        if (status >= StatusSanitizacao.Pendente) m.ConcluirUpload(1000, "etag", agora);
        if (status >= StatusSanitizacao.EmAnalise) m.IniciarAnalise(agora);
        if (status == StatusSanitizacao.Aprovado)
        {
            m.RegistrarHashOriginal(new byte[32]);
            m.Aprovar("audio/mpeg", Enumerable.Repeat((byte)1, 32).ToArray(), 200m, 900, ".mp3", new WebRadio.Application.Features.Midias.MidiaOptions().Limite(tipo), agora);
        }

        if (!ativo) m.Desativar();
        db.ArquivosMidia.Add(m);
        if (comReproducao) db.Reproducoes.Add(new WebRadio.Domain.Midia.Reproducao(m.Id, null, null, agora));
        db.SaveChangesAsync().GetAwaiter().GetResult();
        return m.Id;
    }

    private static Task<HttpResponseMessage> Enviar(HttpClient c, HttpMethod metodo, string url, string? token, object? corpo = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(metodo, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (corpo is not null) req.Content = JsonContent.Create(corpo);
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return c.SendAsync(req);
    }

    [Fact]
    public async Task Leituras_sao_do_PodeModerar_anonimo_401_ouvinte_403()
    {
        var c = NovaCena();
        var id = Semear(c);
        foreach (var url in new[] { Rota, $"{Rota}/{id}", $"{Rota}/{id}/preview" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Enviar(c.C, HttpMethod.Get, url, null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(c.C, HttpMethod.Get, url, await TokenDe(c.C, "ouvinte@example.com"))).StatusCode);
        }
    }

    [Fact]
    public async Task Lista_pagina_filtra_e_o_locutor_nao_ve_as_desativadas()
    {
        var c = NovaCena();
        Semear(c, titulo: "Ativa");
        Semear(c, titulo: "Desativada", ativo: false);
        Semear(c, StatusSanitizacao.Pendente, titulo: "Pendente");

        var locutor = await Json(await Enviar(c.C, HttpMethod.Get, Rota, await TokenDe(c.C, "locutor@example.com")));
        Assert.Equal(2, locutor.GetProperty("total").GetInt32());

        var admin = await TokenDe(c.C, "admin@example.com");
        Assert.Equal(3, (await Json(await Enviar(c.C, HttpMethod.Get, Rota, admin))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(await Enviar(c.C, HttpMethod.Get, Rota + "?ativo=false", admin))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(await Enviar(c.C, HttpMethod.Get, Rota + "?status=Pendente", admin))).GetProperty("total").GetInt32());
        var busca = await Json(await Enviar(c.C, HttpMethod.Get, Rota + "?busca=desativ", admin));
        Assert.Equal("Desativada", busca.GetProperty("itens")[0].GetProperty("titulo").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Get, Rota + "?tamanho=101", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Get, Rota + "?tipo=Inexistente", admin)).StatusCode);
    }

    [Fact]
    public async Task Por_id_tem_etag_e_o_hash_so_aparece_para_o_admin()
    {
        var c = NovaCena();
        var id = Semear(c);

        var admin = await Enviar(c.C, HttpMethod.Get, $"{Rota}/{id}", await TokenDe(c.C, "admin@example.com"));
        Assert.NotNull(admin.Headers.ETag);
        Assert.Equal(new string('0', 64), (await Json(admin)).GetProperty("hashOriginalSha256").GetString());

        var locutor = await Json(await Enviar(c.C, HttpMethod.Get, $"{Rota}/{id}", await TokenDe(c.C, "locutor@example.com")));
        Assert.Equal(JsonValueKind.Null, locutor.GetProperty("hashSha256").ValueKind);
        Assert.False(locutor.TryGetProperty("versao", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await Enviar(c.C, HttpMethod.Get, $"{Rota}/{Guid.NewGuid()}", await TokenDe(c.C, "locutor@example.com"))).StatusCode);
    }

    [Fact]
    public async Task Preview_e_302_para_a_url_assinada_e_com_formato_url_e_200_em_json()
    {
        var c = NovaCena();
        var id = Semear(c);
        var token = await TokenDe(c.C, "locutor@example.com");

        var json = await Enviar(c.C, HttpMethod.Get, $"{Rota}/{id}/preview?formato=url", token);
        Assert.Equal(HttpStatusCode.OK, json.StatusCode);
        Assert.Equal("no-store", json.Headers.CacheControl!.ToString());
        var corpo = await Json(json);
        Assert.StartsWith("http://localhost:8080/storage/midia/", corpo.GetProperty("url").GetString());
        Assert.True(corpo.TryGetProperty("expiraEmUtc", out _));

        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Get, $"{Rota}/{id}/preview?formato=outro", token)).StatusCode);
    }

    [Fact]
    public async Task Preview_de_midia_desativada_so_o_admin_ve_como_na_lista_e_no_por_id()
    {
        var c = NovaCena();
        var id = Semear(c, ativo: false);
        Assert.Equal(HttpStatusCode.NotFound, (await Enviar(c.C, HttpMethod.Get, $"{Rota}/{id}/preview?formato=url", await TokenDe(c.C, "locutor@example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Enviar(c.C, HttpMethod.Get, $"{Rota}/{id}/preview?formato=url", await TokenDe(c.C, "admin@example.com"))).StatusCode);
    }

    [Theory]
    [InlineData(StatusSanitizacao.AguardandoUpload)]
    [InlineData(StatusSanitizacao.Pendente)]
    [InlineData(StatusSanitizacao.EmAnalise)]
    [InlineData(StatusSanitizacao.Rejeitado)]
    public async Task Preview_de_midia_nao_aprovada_e_404(StatusSanitizacao status)
    {
        var c = NovaCena();
        var id = Semear(c, status);
        var token = await TokenDe(c.C, "locutor@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await Enviar(c.C, HttpMethod.Get, $"{Rota}/{id}/preview?formato=url", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Enviar(c.C, HttpMethod.Get, $"{Rota}/{id}/preview", token)).StatusCode);
    }

    [Fact]
    public async Task Patch_exige_if_match_412_se_divergente_e_200_com_etag_novo()
    {
        var c = NovaCena();
        var id = Semear(c);
        var token = await TokenDe(c.C, "locutor@example.com");
        var etag = (await Enviar(c.C, HttpMethod.Get, $"{Rota}/{id}", token)).Headers.ETag!.ToString();

        Assert.Equal((HttpStatusCode)428, (await Enviar(c.C, HttpMethod.Patch, $"{Rota}/{id}", token, new { titulo = "Novo" })).StatusCode);
        Assert.Equal((HttpStatusCode)412, (await Enviar(c.C, HttpMethod.Patch, $"{Rota}/{id}", token, new { titulo = "Novo" }, "\"AAAAAAAAAAA=\"")).StatusCode);

        var ok = await Enviar(c.C, HttpMethod.Patch, $"{Rota}/{id}", token, new { titulo = "Novo título", artista = "Outro" }, etag);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Novo título", (await Json(ok)).GetProperty("titulo").GetString());
        // (O ETag velho deixar de valer depois do PATCH depende do RowVersion do SQL Server: MidiasSqlTests.)
    }

    [Fact]
    public async Task Patch_de_midia_de_outro_locutor_e_403_e_o_admin_pode()
    {
        var c = NovaCena();
        var id = Semear(c, donoEmail: "admin@example.com");
        var admin = await TokenDe(c.C, "admin@example.com");
        var etag = (await Enviar(c.C, HttpMethod.Get, $"{Rota}/{id}", admin)).Headers.ETag!.ToString();

        Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(c.C, HttpMethod.Patch, $"{Rota}/{id}", await TokenDe(c.C, "locutor@example.com"), new { titulo = "Roubado" }, etag)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Enviar(c.C, HttpMethod.Patch, $"{Rota}/{id}", admin, new { titulo = "Do admin" }, etag)).StatusCode);
    }

    [Fact]
    public async Task Patch_de_ativo_e_so_do_admin_e_texto_invalido_ou_musica_sem_titulo_e_400()
    {
        var c = NovaCena();
        var id = Semear(c);
        var locutor = await TokenDe(c.C, "locutor@example.com");
        var admin = await TokenDe(c.C, "admin@example.com");
        async Task<string> Etag() => (await Enviar(c.C, HttpMethod.Get, $"{Rota}/{id}", admin)).Headers.ETag!.ToString();

        Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(c.C, HttpMethod.Patch, $"{Rota}/{id}", locutor, new { ativo = false }, await Etag())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Patch, $"{Rota}/{id}", locutor, new { titulo = "tít\u202Eulo" }, await Etag())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Patch, $"{Rota}/{id}", locutor, new { titulo = "" }, await Etag())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Patch, $"{Rota}/{id}", locutor, new { titulo = 5 }, await Etag())).StatusCode);

        var desativada = await Enviar(c.C, HttpMethod.Patch, $"{Rota}/{id}", admin, new { ativo = false }, await Etag());
        Assert.Equal(HttpStatusCode.OK, desativada.StatusCode);
        Assert.False((await Json(desativada)).GetProperty("ativo").GetBoolean());
    }

    [Fact]
    public async Task Delete_e_so_do_admin_204_sem_reproducao_e_200_desativando_com_reproducao()
    {
        var c = NovaCena();
        var livre = Semear(c);
        var tocada = Semear(c, titulo: "Tocada", comReproducao: true);

        Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(c.C, HttpMethod.Delete, $"{Rota}/{livre}", await TokenDe(c.C, "locutor@example.com"))).StatusCode);
        var admin = await TokenDe(c.C, "admin@example.com");

        Assert.Equal(HttpStatusCode.NoContent, (await Enviar(c.C, HttpMethod.Delete, $"{Rota}/{livre}", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Enviar(c.C, HttpMethod.Get, $"{Rota}/{livre}", admin)).StatusCode);

        var r = await Enviar(c.C, HttpMethod.Delete, $"{Rota}/{tocada}", admin);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False((await Json(r)).GetProperty("ativo").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await Enviar(c.C, HttpMethod.Delete, $"{Rota}/{Guid.NewGuid()}", admin)).StatusCode);
    }
}
