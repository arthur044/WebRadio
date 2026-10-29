using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using Xunit;
using static WebRadio.Api.IntegrationTests.ApiFactoryExtensions;

namespace WebRadio.Api.IntegrationTests;

/// <summary>
/// E3-F01 a F03 no nível HTTP (02 §5 e a matriz RBAC do §1). O SQL do programa da janela, do teto e do fallback é provado em
/// WebRadio.Infrastructure.Tests; aqui o banco é InMemory e as consultas são falsas. O resto é a composição real: pipeline
/// (Validation antes do Throttle), throttle em memória, tratador de exceções, autorização e logs.
/// </summary>
[Collection(ColecaoDeLogs.Nome)]
public class PedidosEndpointsTests
{
    private const string Rota = "/api/v1/pedidos";
    private const string IpDoOuvinte = "203.0.113.31";

    private sealed class ConsultasFalsas : IConsultasDePedidos
    {
        public Guid? Programa { get; set; }

        public Task<Guid?> ProgramaDaJanelaAsync(CancellationToken ct) => Task.FromResult(Programa);

        public Task<bool> TetoDePendentesAtingidoAsync(int teto, CancellationToken ct) => Task.FromResult(false);

        public Task<EsperaDoThrottle> EsperaDoFallbackAsync(byte[] deviceHash, byte[] ipHash, Guid? usuarioId, int limitePorIp, CancellationToken ct) =>
            Task.FromResult(EsperaDoThrottle.Livre);
    }

    private sealed class Captura : ILogEventSink
    {
        private readonly List<LogEvent> _eventos = [];

        public void Emit(LogEvent e) { lock (_eventos) _eventos.Add(e); }

        public string Tudo()
        {
            lock (_eventos)
                return string.Join("\n", _eventos.Select(e => e.RenderMessage() + " " + string.Join(" ", e.Properties.Select(p => p.Value.ToString())) + " " + e.Exception));
        }
    }

    private sealed record Cena(ApiFactory F, HttpClient C, Captura Log, ConsultasFalsas Consultas);

    private static Cena NovaCena(IPAddress? ip = null)
    {
        var f = new ApiFactory { RemoteIp = ip ?? IPAddress.Parse(IpDoOuvinte) };
        var consultas = new ConsultasFalsas();
        var log = new Captura();
        var host = f.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddScoped<IConsultasDePedidos>(_ => consultas);
            s.AddSingleton<ILogEventSink>(log);
        }));
        CriarUsuario(host.Services, "locutor@example.com", Role.Locutor);
        CriarUsuario(host.Services, "ouvinte@example.com", Role.Ouvinte);
        return new Cena(f, host.CreateClient(new() { BaseAddress = new Uri("http://localhost") }), log, consultas);
    }

    /// <summary>Pelo host derivado (o que tem o sink de log), nunca pelo <c>ApiFactory.Services</c> base: ver <see cref="ColecaoDeLogs"/>.</summary>
    private static void CriarUsuario(IServiceProvider servicos, string email, Role role)
    {
        using var escopo = servicos.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<IRadioDbContext>();
        var hasher = escopo.ServiceProvider.GetRequiredService<ISenhaHasher>();
        db.Usuarios.Add(new WebRadio.Domain.Usuarios.Usuario(Guid.NewGuid(), "Fulano", email, hasher.Hash(Senha), role, false, DateTime.UtcNow));
        db.SaveChangesAsync().GetAwaiter().GetResult();
    }

    private static async Task<string> TokenDe(HttpClient c, string email)
    {
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new { email, senha = Senha });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static Task<HttpResponseMessage> Enviar(HttpClient c, HttpMethod metodo, string url, object? corpo = null, Guid? ouvinte = null, string? token = null, string? headerBruto = null)
    {
        var req = new HttpRequestMessage(metodo, url);
        if (ouvinte is { } id) req.Headers.Add("X-Listener-Id", id.ToString("D"));
        if (headerBruto is not null) req.Headers.TryAddWithoutValidation("X-Listener-Id", headerBruto);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (corpo is not null) req.Content = JsonContent.Create(corpo);
        return c.SendAsync(req);
    }

    private static object Pedido(string nome = "Maria", string titulo = "Garota de Ipanema", string artista = "Tom Jobim", string? mensagem = "Para a minha mãe") =>
        new { nomeOuvinte = nome, tituloMusica = titulo, artista, mensagem };

    // Como string: o mesmo corpo é lido mais de uma vez (tipo do erro e depois os campos), e o stream do HttpContent só serve uma.
    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task<string> TipoDoErro(HttpResponseMessage r) => (await Json(r)).GetProperty("type").GetString()!;

    // ---- POST /pedidos ----

    [Fact]
    public async Task Anonimo_com_X_Listener_Id_cria_o_pedido_e_recebe_201_sem_hash_nem_usuario()
    {
        var c = NovaCena();
        c.Consultas.Programa = Guid.NewGuid();

        var r = await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var corpo = await Json(r);
        Assert.Equal("Pendente", corpo.GetProperty("status").GetString());
        Assert.Equal("Maria", corpo.GetProperty("nomeOuvinte").GetString());
        Assert.Equal(c.Consultas.Programa, corpo.GetProperty("programaId").GetGuid());
        var texto = corpo.GetRawText();
        Assert.DoesNotContain("hash", texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("usuarioId", texto, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nao-e-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("7B0E7C2E111142228333000000000001")]
    [InlineData("{7b0e7c2e-1111-4222-8333-000000000001}")]
    public async Task X_Listener_Id_ausente_invalido_ou_zerado_e_400(string? header)
    {
        var c = NovaCena();

        var r = await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), headerBruto: header);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("/erros/validacao", await TipoDoErro(r));
        Assert.True((await Json(r)).GetProperty("errors").TryGetProperty("X-Listener-Id", out _));
    }

    [Fact]
    public async Task Dois_X_Listener_Id_na_mesma_requisicao_e_400()
    {
        var c = NovaCena();
        var req = new HttpRequestMessage(HttpMethod.Post, Rota) { Content = JsonContent.Create(Pedido()) };
        req.Headers.Add("X-Listener-Id", [Guid.NewGuid().ToString(), Guid.NewGuid().ToString()]);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.C.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Segundo_pedido_do_mesmo_dispositivo_e_429_com_Retry_After_e_retryAfterSeconds_iguais()
    {
        var c = NovaCena();
        var ouvinte = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), ouvinte)).StatusCode);

        var r = await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), ouvinte);

        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.Equal("/erros/muitos-pedidos", await TipoDoErro(r));
        var segundos = (await Json(r)).GetProperty("retryAfterSeconds").GetInt32();
        Assert.InRange(segundos, 1, 300);
        Assert.Equal(segundos.ToString(), r.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString());
        Assert.StartsWith("application/problem+json", r.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task O_quarto_pedido_de_dispositivos_diferentes_no_mesmo_IP_e_429_e_outro_IP_passa()
    {
        var c = NovaCena();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Created, (await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), Guid.NewGuid())).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), Guid.NewGuid())).StatusCode);

        c.F.RemoteIp = IPAddress.Parse("198.51.100.20");
        Assert.Equal(HttpStatusCode.Created, (await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task X_Forwarded_For_forjado_nao_muda_o_IP_visto_pelo_throttle()
    {
        var c = NovaCena();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Created, (await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), Guid.NewGuid())).StatusCode);

        // O socket (203.0.113.31) não está na rede do proxy: o X-Forwarded-For do cliente é ignorado.
        var req = new HttpRequestMessage(HttpMethod.Post, Rota) { Content = JsonContent.Create(Pedido()) };
        req.Headers.Add("X-Listener-Id", Guid.NewGuid().ToString());
        req.Headers.Add("X-Forwarded-For", "192.0.2.200");

        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.C.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Entrada_invalida_e_400_e_nao_gasta_a_vaga_do_dispositivo()
    {
        var c = NovaCena();
        var ouvinte = Guid.NewGuid();

        var invalido = await Enviar(c.C, HttpMethod.Post, Rota, Pedido(nome: "Maria\nSilva"), ouvinte);
        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);

        // A validação vem antes do throttle: o mesmo dispositivo ainda tem a vaga de 5 min.
        Assert.Equal(HttpStatusCode.Created, (await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), ouvinte)).StatusCode);
    }

    [Fact]
    public async Task Nome_com_60_emoji_e_400_e_nao_500()
    {
        var c = NovaCena();

        var r = await Enviar(c.C, HttpMethod.Post, Rota, Pedido(nome: string.Concat(Enumerable.Repeat("\U0001F600", 60))), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.True((await Json(r)).GetProperty("errors").TryGetProperty("NomeOuvinte", out _));
    }

    [Fact]
    public async Task Corpo_acima_de_8_KB_e_413_antes_de_validar_o_texto_e_sem_gastar_vaga()
    {
        var c = NovaCena();
        var ouvinte = Guid.NewGuid();

        var r = await Enviar(c.C, HttpMethod.Post, Rota, Pedido(nome: new string('a', 9 * 1024)), ouvinte);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Equal("/erros/corpo-grande-demais", await TipoDoErro(r));
        Assert.Equal(HttpStatusCode.Created, (await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), ouvinte)).StatusCode);
    }

    [Fact]
    public async Task Campo_de_texto_acima_do_limite_mas_dentro_dos_8_KB_e_400()
    {
        var c = NovaCena();

        var r = await Enviar(c.C, HttpMethod.Post, Rota, Pedido(nome: new string('a', 4000)), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Json_vazio_e_400_nao_500()
    {
        var c = NovaCena();

        var r = await Enviar(c.C, HttpMethod.Post, Rota, new { }, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Com_Bearer_o_pedido_e_ligado_ao_usuario_e_aparece_nos_meus_pedidos_dele()
    {
        var c = NovaCena();
        var token = await TokenDe(c.C, "ouvinte@example.com");

        Assert.Equal(HttpStatusCode.Created, (await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), Guid.NewGuid(), token)).StatusCode);

        // Sem X-Listener-Id: com Bearer o usuário basta.
        var meus = await Enviar(c.C, HttpMethod.Get, Rota + "/meus", token: token);
        Assert.Equal(HttpStatusCode.OK, meus.StatusCode);
        Assert.Single((await Json(meus)).EnumerateArray());
    }

    [Fact]
    public async Task Nenhum_IP_nem_listenerId_aparece_nos_logs_da_requisicao()
    {
        var c = NovaCena();
        var ouvinte = Guid.NewGuid();
        await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), ouvinte);
        await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), ouvinte); // 429
        await Enviar(c.C, HttpMethod.Post, Rota, Pedido(nome: "a\nb"), Guid.NewGuid()); // 400
        await Enviar(c.C, HttpMethod.Get, Rota + "/meus", ouvinte: ouvinte);

        var logs = c.Log.Tudo();
        Assert.NotEmpty(logs);
        Assert.DoesNotContain(IpDoOuvinte, logs);
        Assert.DoesNotContain(ouvinte.ToString("D"), logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ouvinte.ToString("N"), logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Garota de Ipanema", logs);
    }

    // ---- GET /pedidos/meus ----

    [Fact]
    public async Task Meus_pedidos_devolve_um_array_sem_paginacao_e_so_do_dispositivo()
    {
        var c = NovaCena();
        var ouvinte = Guid.NewGuid();
        await Enviar(c.C, HttpMethod.Post, Rota, Pedido(nome: "Da A"), ouvinte);
        await Enviar(c.C, HttpMethod.Post, Rota, Pedido(nome: "Da B"), Guid.NewGuid());

        var r = await Enviar(c.C, HttpMethod.Get, Rota + "/meus", ouvinte: ouvinte);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var corpo = await Json(r);
        Assert.Equal(JsonValueKind.Array, corpo.ValueKind);
        Assert.Equal("Da A", Assert.Single(corpo.EnumerateArray()).GetProperty("nomeOuvinte").GetString());
    }

    [Fact]
    public async Task Meus_pedidos_sem_X_Listener_Id_nem_Bearer_e_400()
    {
        var c = NovaCena();

        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Get, Rota + "/meus")).StatusCode);
    }

    // ---- GET /pedidos (fila) e RBAC ----

    [Fact]
    public async Task Fila_anonimo_401_ouvinte_403_locutor_200()
    {
        var c = NovaCena();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Enviar(c.C, HttpMethod.Get, Rota)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(c.C, HttpMethod.Get, Rota, token: await TokenDe(c.C, "ouvinte@example.com"))).StatusCode);
        var r = await Enviar(c.C, HttpMethod.Get, Rota, token: await TokenDe(c.C, "locutor@example.com"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var corpo = await Json(r);
        Assert.Equal(0, corpo.GetProperty("total").GetInt32());
        Assert.Equal(1, corpo.GetProperty("pagina").GetInt32());
        Assert.Equal(20, corpo.GetProperty("tamanho").GetInt32());
    }

    [Theory]
    [InlineData("Rejeitado")]
    [InlineData("Tocado")]
    [InlineData("Expirado")]
    [InlineData("xyz")]
    public async Task Fila_com_status_fora_de_Pendente_e_Aprovado_e_400(string status)
    {
        var c = NovaCena();

        var r = await Enviar(c.C, HttpMethod.Get, Rota + "?status=" + status, token: await TokenDe(c.C, "locutor@example.com"));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Fila_mostra_o_pedido_criado_em_Pendente()
    {
        var c = NovaCena();
        await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), Guid.NewGuid());

        var r = await Enviar(c.C, HttpMethod.Get, Rota + "?status=Pendente&pagina=1&tamanho=100", token: await TokenDe(c.C, "locutor@example.com"));

        var corpo = await Json(r);
        Assert.Equal(1, corpo.GetProperty("total").GetInt32());
        Assert.Equal("Maria", corpo.GetProperty("itens")[0].GetProperty("nomeOuvinte").GetString());
    }

    [Fact]
    public async Task Tamanho_acima_de_100_e_400()
    {
        var c = NovaCena();

        var r = await Enviar(c.C, HttpMethod.Get, Rota + "?tamanho=101", token: await TokenDe(c.C, "locutor@example.com"));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // ---- moderação ----

    private static async Task<Guid> CriarPendente(Cena c)
    {
        var r = await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), Guid.NewGuid());
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    [Theory]
    [InlineData("aprovar")]
    [InlineData("rejeitar")]
    [InlineData("marcar-tocado")]
    public async Task Moderar_anonimo_401_e_ouvinte_403(string acao)
    {
        var c = NovaCena();
        var id = await CriarPendente(c);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/{acao}", new { motivo = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/{acao}", new { motivo = "x" }, token: await TokenDe(c.C, "ouvinte@example.com"))).StatusCode);
    }

    [Fact]
    public async Task Locutor_aprova_e_depois_marca_tocado_e_o_segundo_aprovar_e_409_com_tipo_proprio()
    {
        var c = NovaCena();
        var id = await CriarPendente(c);
        var token = await TokenDe(c.C, "locutor@example.com");

        var aprovado = await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/aprovar", token: token);
        Assert.Equal(HttpStatusCode.OK, aprovado.StatusCode);
        Assert.Equal("Aprovado", (await Json(aprovado)).GetProperty("status").GetString());

        var repetido = await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/aprovar", token: token);
        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);
        Assert.Equal("/erros/pedido-ja-moderado", await TipoDoErro(repetido));

        var tocado = await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/marcar-tocado", token: token);
        Assert.Equal(HttpStatusCode.OK, tocado.StatusCode);
        Assert.Equal("Tocado", (await Json(tocado)).GetProperty("status").GetString());

        var denovo = await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/marcar-tocado", token: token);
        Assert.Equal("/erros/pedido-ja-moderado", await TipoDoErro(denovo));
    }

    [Fact]
    public async Task Aprovar_com_midiaId_e_400_no_Epico_3_e_o_pedido_continua_Pendente()
    {
        var c = NovaCena();
        var id = await CriarPendente(c);
        var token = await TokenDe(c.C, "locutor@example.com");

        var r = await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/aprovar", new { midiaId = Guid.NewGuid() }, token: token);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var fila = await Json(await Enviar(c.C, HttpMethod.Get, Rota, token: token));
        Assert.Equal(1, fila.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Marcar_tocado_de_um_Pendente_e_409_pedido_ja_moderado()
    {
        var c = NovaCena();
        var id = await CriarPendente(c);

        var r = await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/marcar-tocado", token: await TokenDe(c.C, "locutor@example.com"));

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("/erros/pedido-ja-moderado", await TipoDoErro(r));
    }

    [Fact]
    public async Task Rejeitar_exige_motivo_de_1_a_280_e_grava_o_motivo()
    {
        var c = NovaCena();
        var id = await CriarPendente(c);
        var token = await TokenDe(c.C, "locutor@example.com");

        foreach (var ruim in new[] { "", "   ", "linha1\nlinha2", new string('x', 281) })
            Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/rejeitar", new { motivo = ruim }, token: token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/rejeitar", new { }, token: token)).StatusCode);

        var r = await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/rejeitar", new { motivo = "Fora do estilo da casa" }, token: token);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var corpo = await Json(r);
        Assert.Equal("Rejeitado", corpo.GetProperty("status").GetString());
        Assert.Equal("Fora do estilo da casa", corpo.GetProperty("motivoRejeicao").GetString());
    }

    [Fact]
    public async Task Moderar_pedido_inexistente_e_404()
    {
        var c = NovaCena();

        var r = await Enviar(c.C, HttpMethod.Post, $"{Rota}/{Guid.NewGuid()}/aprovar", token: await TokenDe(c.C, "locutor@example.com"));

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task A_moderacao_grava_o_PedidoModerado_no_outbox_para_o_grupo_do_ouvinte_e_os_locutores()
    {
        var c = NovaCena();
        var ouvinte = Guid.NewGuid();
        var criado = await Json(await Enviar(c.C, HttpMethod.Post, Rota, Pedido(), ouvinte));
        var id = criado.GetProperty("id").GetGuid();
        await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/aprovar", token: await TokenDe(c.C, "locutor@example.com"));

        await using var escopo = c.F.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<WebRadio.Infrastructure.Persistencia.RadioDbContext>();
        var identidade = escopo.ServiceProvider.GetRequiredService<IListenerIdentity>();
        var moderado = db.EventosOutbox.ToList().Single(e => e.Tipo == "PedidoModerado");
        using var doc = JsonDocument.Parse(moderado.Payload);
        Assert.Equal(["locutores", identidade.GrupoListener(ouvinte)],
            doc.RootElement.GetProperty("grupos").EnumerateArray().Select(g => g.GetString()));
    }
}
