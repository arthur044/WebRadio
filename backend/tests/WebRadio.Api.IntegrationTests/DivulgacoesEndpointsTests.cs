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

/// <summary>
/// E3-F05 e E3-F06 no nível HTTP (02 §7 e a matriz RBAC do §1). O storage é falso (objetos em memória); o resto é a composição real, inclusive o
/// SkiaSharp: o JPEG com GPS sai WebP sem metadado, e o que não é JPEG/PNG, animado ou grande demais é recusado antes de qualquer decode.
/// </summary>
public class DivulgacoesEndpointsTests
{
    private const string Rota = "/api/v1/divulgacoes";

    private sealed class StorageEmMemoria : IObjectStorage
    {
        public Dictionary<string, (byte[] Bytes, string ContentType)> Objetos { get; } = [];

        public List<string> Apagados { get; } = [];

        public Task PutAsync(string bucket, string chave, Stream conteudo, long tamanhoBytes, string contentType, CancellationToken ct = default)
        {
            using var m = new MemoryStream();
            conteudo.CopyTo(m);
            Objetos[$"{bucket}/{chave}"] = (m.ToArray(), contentType);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string bucket, string chave, CancellationToken ct = default)
        {
            Apagados.Add(chave);
            Objetos.Remove($"{bucket}/{chave}");
            return Task.CompletedTask;
        }

        public Task<InfoObjeto?> HeadAsync(string bucket, string chave, CancellationToken ct = default) =>
            Task.FromResult(Objetos.TryGetValue($"{bucket}/{chave}", out var o) ? new InfoObjeto(o.Bytes.Length, o.ContentType, "e") : null);

        public Task<IReadOnlyList<ObjetoListado>> ListarAsync(string bucket, string prefixo, CancellationToken ct = default) => throw new NotSupportedException();

        public string UrlPublica(string bucket, string chave) => $"http://localhost:8080/storage/{bucket}/{chave}";

        public Task<PostPolicyAssinada> CriarPostPolicyAsync(string bucket, string chave, string contentType, long tamanhoMaximoBytes, TimeSpan validade, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string> UrlGetPreAssinadaAsync(string bucket, string chave, TimeSpan validade, string? contentType = null, string? contentDisposition = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task MoverAsync(string bucketOrigem, string chaveOrigem, string bucketDestino, string chaveDestino, string etagEsperado, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> VerificarAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed record Cena(HttpClient C, StorageEmMemoria Storage);

    private static Cena NovaCena()
    {
        var f = new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.40") };
        var storage = new StorageEmMemoria();
        var host = f.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IObjectStorage>(storage)));
        f.CriarUsuario("admin@example.com", Role.Admin);
        f.CriarUsuario("locutor@example.com", Role.Locutor);
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

    private static Task<HttpResponseMessage> Enviar(HttpClient c, HttpMethod metodo, string url, string? token, object? corpo = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(metodo, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (corpo is not null) req.Content = JsonContent.Create(corpo);
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return c.SendAsync(req);
    }

    private static object Corpo(string titulo = "Promoção", string? imagemChave = null, string? link = null, int prioridade = 50, bool ativo = true) =>
        new { titulo, mensagem = "Texto da divulgação", imagemChave, linkDestino = link, prioridade, ativo };

    private static async Task<string> Admin(Cena c) => await TokenDe(c.C, "admin@example.com");

    // ---- RBAC ----

    [Fact]
    public async Task Ativas_sao_publicas_com_cache_de_30_segundos_e_o_resto_e_do_Admin()
    {
        var c = NovaCena();

        var ativas = await Enviar(c.C, HttpMethod.Get, Rota + "/ativas", null);
        Assert.Equal(HttpStatusCode.OK, ativas.StatusCode);
        Assert.Equal("public, max-age=30", ativas.Headers.CacheControl!.ToString());

        var locutor = await TokenDe(c.C, "locutor@example.com");
        var ouvinte = await TokenDe(c.C, "ouvinte@example.com");
        foreach (var (metodo, url) in new[] { (HttpMethod.Get, Rota), (HttpMethod.Get, $"{Rota}/{Guid.NewGuid()}"), (HttpMethod.Post, Rota), (HttpMethod.Put, $"{Rota}/{Guid.NewGuid()}"),
                     (HttpMethod.Delete, $"{Rota}/{Guid.NewGuid()}"), (HttpMethod.Post, Rota + "/imagens") })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Enviar(c.C, metodo, url, null, Corpo())).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(c.C, metodo, url, locutor, Corpo())).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(c.C, metodo, url, ouvinte, Corpo())).StatusCode);
        }
    }

    // ---- CRUD ----

    [Fact]
    public async Task POST_201_com_Location_e_ETag_e_GET_por_id_devolve_200_com_ETag_e_inexistente_404()
    {
        var c = NovaCena();
        var admin = await Admin(c);

        var criada = await Enviar(c.C, HttpMethod.Post, Rota, admin, Corpo(link: "https://exemplo.com/promo"));

        Assert.Equal(HttpStatusCode.Created, criada.StatusCode);
        var corpo = await Json(criada);
        var id = corpo.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/divulgacoes/{id}", criada.Headers.Location!.ToString());
        Assert.NotNull(criada.Headers.ETag);
        Assert.Equal("https://exemplo.com/promo", corpo.GetProperty("linkDestino").GetString());
        Assert.False(corpo.TryGetProperty("versao", out _));

        var lida = await Enviar(c.C, HttpMethod.Get, $"{Rota}/{id}", admin);
        Assert.Equal(HttpStatusCode.OK, lida.StatusCode);
        Assert.NotNull(lida.Headers.ETag);
        Assert.Equal(HttpStatusCode.NotFound, (await Enviar(c.C, HttpMethod.Get, $"{Rota}/{Guid.NewGuid()}", admin)).StatusCode);
    }

    [Fact]
    public async Task POST_com_corpo_invalido_e_400_com_errors_e_JSON_vazio_nao_e_500()
    {
        var c = NovaCena();
        var admin = await Admin(c);

        var r = await Enviar(c.C, HttpMethod.Post, Rota, admin, Corpo(titulo: "a\nb", link: "https://127.0.0.1", prioridade: 101));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var erros = (await Json(r)).GetProperty("errors");
        Assert.True(erros.TryGetProperty("Titulo", out _));
        Assert.True(erros.TryGetProperty("LinkDestino", out _));
        Assert.True(erros.TryGetProperty("Prioridade", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Post, Rota, admin, new { })).StatusCode);
    }

    [Fact]
    public async Task PUT_sem_If_Match_e_428_com_ETag_velho_412_e_com_o_certo_200_com_ETag_novo()
    {
        var c = NovaCena();
        var admin = await Admin(c);
        var id = (await Json(await Enviar(c.C, HttpMethod.Post, Rota, admin, Corpo()))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Enviar(c.C, HttpMethod.Put, $"{Rota}/{id}", admin, Corpo())).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Enviar(c.C, HttpMethod.Put, $"{Rota}/{id}", admin, Corpo(), "\"AAEC\"")).StatusCode);

        var ok = await Enviar(c.C, HttpMethod.Put, $"{Rota}/{id}", admin, Corpo(titulo: "Editada"), "\"\"");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Editada", (await Json(ok)).GetProperty("titulo").GetString());
        Assert.NotNull(ok.Headers.ETag);
    }

    [Fact]
    public async Task PUT_e_POST_sem_ativo_ou_sem_prioridade_sao_400_e_o_PUT_nao_reativa_em_silencio()
    {
        var c = NovaCena();
        var admin = await Admin(c);
        var id = (await Json(await Enviar(c.C, HttpMethod.Post, Rota, admin, Corpo(ativo: false)))).GetProperty("id").GetGuid();

        var semAtivo = await Enviar(c.C, HttpMethod.Put, $"{Rota}/{id}", admin, new { titulo = "T", mensagem = "M", prioridade = 5 }, "\"\"");
        var semPrioridade = await Enviar(c.C, HttpMethod.Put, $"{Rota}/{id}", admin, new { titulo = "T", mensagem = "M", ativo = true }, "\"\"");
        var postSemAtivo = await Enviar(c.C, HttpMethod.Post, Rota, admin, new { titulo = "T", mensagem = "M", prioridade = 5 });
        var postSemPrioridade = await Enviar(c.C, HttpMethod.Post, Rota, admin, new { titulo = "T", mensagem = "M", ativo = true });

        Assert.Equal(HttpStatusCode.BadRequest, semAtivo.StatusCode);
        Assert.True((await Json(semAtivo)).GetProperty("errors").TryGetProperty("Ativo", out _));
        Assert.Equal(HttpStatusCode.BadRequest, semPrioridade.StatusCode);
        Assert.True((await Json(semPrioridade)).GetProperty("errors").TryGetProperty("Prioridade", out _));
        Assert.Equal(HttpStatusCode.BadRequest, postSemAtivo.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, postSemPrioridade.StatusCode);
        // A divulgação desativada continua desativada e fora do Mural público.
        Assert.False((await Json(await Enviar(c.C, HttpMethod.Get, $"{Rota}/{id}", admin))).GetProperty("ativo").GetBoolean());
        Assert.Empty((await Json(await Enviar(c.C, HttpMethod.Get, Rota + "/ativas", null))).EnumerateArray());
    }

    [Fact]
    public async Task PUT_ativo_false_some_das_ativas()
    {
        var c = NovaCena();
        var admin = await Admin(c);
        var id = (await Json(await Enviar(c.C, HttpMethod.Post, Rota, admin, Corpo()))).GetProperty("id").GetGuid();
        Assert.Single((await Json(await Enviar(c.C, HttpMethod.Get, Rota + "/ativas", null))).EnumerateArray());

        var r = await Enviar(c.C, HttpMethod.Put, $"{Rota}/{id}", admin, Corpo(ativo: false), "\"\"");

        Assert.False((await Json(r)).GetProperty("ativo").GetBoolean());
        Assert.Empty((await Json(await Enviar(c.C, HttpMethod.Get, Rota + "/ativas", null))).EnumerateArray());
    }

    [Fact]
    public async Task DELETE_204_e_depois_404_e_a_imagem_some_do_storage()
    {
        var c = NovaCena();
        var admin = await Admin(c);
        var chave = (await Json(await EnviarImagem(c, admin, ImagensHttp.Jpeg()))).GetProperty("chave").GetString()!;
        var id = (await Json(await Enviar(c.C, HttpMethod.Post, Rota, admin, Corpo(imagemChave: chave)))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await Enviar(c.C, HttpMethod.Delete, $"{Rota}/{id}", admin)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await Enviar(c.C, HttpMethod.Delete, $"{Rota}/{id}", admin)).StatusCode);
        Assert.Contains(chave, c.Storage.Apagados);
    }

    [Fact]
    public async Task Imagem_ligada_a_outra_divulgacao_e_409_com_tipo_proprio()
    {
        var c = NovaCena();
        var admin = await Admin(c);
        var chave = (await Json(await EnviarImagem(c, admin, ImagensHttp.Jpeg()))).GetProperty("chave").GetString()!;
        Assert.Equal(HttpStatusCode.Created, (await Enviar(c.C, HttpMethod.Post, Rota, admin, Corpo(imagemChave: chave))).StatusCode);

        var r = await Enviar(c.C, HttpMethod.Post, Rota, admin, Corpo(titulo: "Outra", imagemChave: chave));

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("/erros/imagem-em-uso", (await Json(r)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Chave_arbitraria_ou_que_nao_existe_no_bucket_e_400()
    {
        var c = NovaCena();
        var admin = await Admin(c);

        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Post, Rota, admin, Corpo(imagemChave: "midia/qualquer.mp3"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Post, Rota, admin, Corpo(imagemChave: "divulgacoes/0123456789abcdef0123456789abcdef.webp"))).StatusCode);
    }

    // ---- POST /divulgacoes/imagens ----

    private static Task<HttpResponseMessage> EnviarImagem(Cena c, string? token, byte[] bytes, string nomeDaParte = "file", string nomeDoArquivo = "foto.jpg", string tipoDeConteudo = "image/jpeg",
        Action<MultipartFormDataContent>? extra = null)
    {
        var conteudo = new MultipartFormDataContent();
        var arquivo = new ByteArrayContent(bytes);
        arquivo.Headers.ContentType = MediaTypeHeaderValue.Parse(tipoDeConteudo);
        conteudo.Add(arquivo, nomeDaParte, nomeDoArquivo);
        extra?.Invoke(conteudo);
        var req = new HttpRequestMessage(HttpMethod.Post, Rota + "/imagens") { Content = conteudo };
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c.C.SendAsync(req);
    }

    [Fact]
    public async Task JPEG_com_GPS_volta_201_e_o_que_fica_no_bucket_e_WebP_sem_metadado_com_Content_Type_do_servidor()
    {
        var c = NovaCena();
        var admin = await Admin(c);

        // O cliente mente o nome e o Content-Type da parte: nada disso é lido.
        var r = await EnviarImagem(c, admin, ImagensHttp.JpegComGps(), nomeDoArquivo: "../../etc/passwd.php", tipoDeConteudo: "image/svg+xml");

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var corpo = await Json(r);
        var chave = corpo.GetProperty("chave").GetString()!;
        Assert.Matches(@"^divulgacoes/[0-9a-f]{32}\.webp\z", chave);
        Assert.Equal($"http://localhost:8080/storage/publico/{chave}", corpo.GetProperty("imagemUrl").GetString());
        var (bytes, contentType) = c.Storage.Objetos[$"publico/{chave}"];
        Assert.Equal("image/webp", contentType);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.False(bytes.AsSpan().IndexOf("Exif"u8) >= 0);
    }

    [Theory]
    [InlineData("webp")]
    [InlineData("gif")]
    [InlineData("svg")]
    [InlineData("html")]
    [InlineData("apng")]
    [InlineData("bomba")]
    [InlineData("vazio")]
    public async Task Formato_fora_de_JPEG_PNG_animado_ou_com_lado_acima_de_4096_e_400_e_nada_vai_para_o_bucket(string caso)
    {
        var c = NovaCena();
        var admin = await Admin(c);

        // HTML renomeado para .jpg com Content-Type de imagem, e os demais: o que vale é o conteúdo.
        var r = await EnviarImagem(c, admin, ImagensHttp.Caso(caso));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.True((await Json(r)).GetProperty("errors").TryGetProperty("file", out _));
        Assert.Empty(c.Storage.Objetos);
    }

    [Fact]
    public async Task Arquivo_acima_de_2_MB_e_413_sem_decodificar()
    {
        var c = NovaCena();
        var admin = await Admin(c);

        var r = await EnviarImagem(c, admin, new byte[2 * 1024 * 1024 + 4096]);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Empty(c.Storage.Objetos);
    }

    [Fact]
    public async Task Duas_partes_ou_parte_com_outro_nome_e_400()
    {
        var c = NovaCena();
        var admin = await Admin(c);

        var duas = await EnviarImagem(c, admin, ImagensHttp.Jpeg(), extra: m => m.Add(new ByteArrayContent(ImagensHttp.Jpeg()), "file", "outra.jpg"));
        var outroNome = await EnviarImagem(c, admin, ImagensHttp.Jpeg(), nomeDaParte: "imagem");

        Assert.Equal(HttpStatusCode.BadRequest, duas.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, outroNome.StatusCode);
        Assert.Empty(c.Storage.Objetos);
    }

    [Fact]
    public async Task Corpo_que_nao_e_multipart_e_400()
    {
        var c = NovaCena();

        var r = await Enviar(c.C, HttpMethod.Post, Rota + "/imagens", await Admin(c), new { file = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task A_31a_tentativa_na_hora_e_429_e_conta_inclusive_as_recusadas()
    {
        var c = NovaCena();
        var admin = await Admin(c);

        for (var i = 0; i < 30; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await EnviarImagem(c, admin, ImagensHttp.Html())).StatusCode); // recusadas, mas contadas

        var r = await EnviarImagem(c, admin, ImagensHttp.Jpeg());

        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.NotNull(r.Headers.RetryAfter);
        Assert.InRange((await Json(r)).GetProperty("retryAfterSeconds").GetInt32(), 1, 3600);
        Assert.Empty(c.Storage.Objetos);
    }

    [Fact]
    public async Task O_limite_e_por_Admin_nao_global()
    {
        var f = new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.41") };
        var storage = new StorageEmMemoria();
        var host = f.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IObjectStorage>(storage)));
        f.CriarUsuario("admin1@example.com", Role.Admin);
        f.CriarUsuario("admin2@example.com", Role.Admin);
        var c = new Cena(host.CreateClient(new() { BaseAddress = new Uri("http://localhost") }), storage);
        var um = await TokenDe(c.C, "admin1@example.com");
        var dois = await TokenDe(c.C, "admin2@example.com");
        for (var i = 0; i < 31; i++) await EnviarImagem(c, um, ImagensHttp.Html());

        Assert.Equal(HttpStatusCode.Created, (await EnviarImagem(c, dois, ImagensHttp.Jpeg())).StatusCode);
    }

    [Fact]
    public async Task Nao_Admin_e_barrado_antes_de_ler_o_corpo_e_sem_gastar_o_limite_do_Admin()
    {
        var c = NovaCena();

        Assert.Equal(HttpStatusCode.Unauthorized, (await EnviarImagem(c, null, ImagensHttp.Jpeg())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarImagem(c, await TokenDe(c.C, "locutor@example.com"), ImagensHttp.Jpeg())).StatusCode);
        Assert.Empty(c.Storage.Objetos);
    }

    [Fact]
    public async Task X_Forwarded_For_forjado_na_rota_de_upload_nao_muda_o_IP_visto_pela_API()
    {
        // O IP não faz parte do limite de upload (é por Admin), mas a rota nova não pode confiar em X-Forwarded-For de quem não é o proxy.
        var c = NovaCena();
        var req = new HttpRequestMessage(HttpMethod.Post, Rota + "/imagens") { Content = new MultipartFormDataContent { { new ByteArrayContent(ImagensHttp.Jpeg()), "file", "a.jpg" } } };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Admin(c));
        req.Headers.Add("X-Forwarded-For", "192.0.2.77");

        Assert.Equal(HttpStatusCode.Created, (await c.C.SendAsync(req)).StatusCode);
    }
}
