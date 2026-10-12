using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Playout;
using WebRadio.Domain.Enums;
using WebRadio.Infrastructure.Persistencia;
using Xunit;
using static WebRadio.Api.IntegrationTests.ApiFactoryExtensions;

namespace WebRadio.Api.IntegrationTests;

/// <summary>
/// E1-F12. A porta do socket é simulada pelo cabeçalho de teste X-Test-LocalPort (o TestServer não tem socket); o bind real
/// do Kestrel na :8081 só é exercitado com o compose (B2).
/// </summary>
public class PlayoutEndpointsTests
{
    private const string Proxima = "/api/v1/internal/playout/proxima";
    private const string Eventos = "/api/v1/internal/playout/eventos";
    private static readonly string Token = new('a', 64);
    private static readonly string TokenAntigo = new('b', 64);

    private static (ApiFactory F, HttpClient C) Novo(Action<ApiFactory>? configurar = null)
    {
        var f = new ApiFactory();
        configurar?.Invoke(f);
        return (f, f.CreateClient(new() { BaseAddress = new Uri("http://localhost") }));
    }

    private static HttpRequestMessage Req(HttpMethod metodo, string url, int porta = 8081, string? token = null, object? corpo = null, string esquema = "Bearer")
    {
        var req = new HttpRequestMessage(metodo, url);
        req.Headers.Add("X-Test-LocalPort", porta.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (token is not null) req.Headers.TryAddWithoutValidation("Authorization", $"{esquema} {token}");
        if (corpo is not null) req.Content = corpo as HttpContent ?? JsonContent.Create(corpo);
        return req;
    }

    private static object Faixa(string? titulo = "Faixa", string? artista = "Banda", string tipo = "FaixaIniciada", object? ocorridoEmUtc = null, Guid? pedidoId = null)
        => new { tipo, titulo, artista, pedidoId, ocorridoEmUtc = ocorridoEmUtc ?? DateTime.UtcNow };

    // ---- listener: pela :8080 a rota não existe ----

    [Theory]
    [InlineData("/api/v1/internal/playout/proxima")]
    [InlineData("/api/v1/INTERNAL/playout/proxima")]
    [InlineData("/api/v1/Internal/Playout/Proxima")]
    [InlineData("/api/v1//internal/playout/proxima")]
    [InlineData("/api/v1/%69nternal/playout/proxima")]
    [InlineData("/api/v1/internal/playout/proxima/")]
    [InlineData("/api/v1/internal")]
    [InlineData("/api/v1/internal/x")]
    [InlineData("/api/v1/INTERNAL/x")]
    [InlineData("/api/v1//internal/x")]
    [InlineData("/api/v1/%69nternal/x")]
    public async Task Pela_porta_publica_qualquer_variante_do_caminho_interno_e_404_mesmo_com_o_token_certo(string caminho)
    {
        var (_, c) = Novo();

        foreach (var metodo in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            var req = Req(metodo, caminho, porta: 8080, token: Token, corpo: metodo == HttpMethod.Post ? Faixa() : null);
            var r = await c.SendAsync(req);
            Assert.True(r.StatusCode == HttpStatusCode.NotFound, $"{metodo} {caminho}: {(int)r.StatusCode} {r.Headers.WwwAuthenticate} {await r.Content.ReadAsStringAsync()}");
        }
    }

    [Fact]
    public async Task Host_dizendo_8081_na_porta_publica_continua_404_a_checagem_e_pela_porta_do_socket()
    {
        var (_, c) = Novo();
        var req = Req(HttpMethod.Get, Proxima, porta: 8080, token: Token);
        req.Headers.Host = "localhost:8081";

        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Pela_porta_publica_sem_token_tambem_e_404_e_nao_401_a_rota_nao_se_revela()
    {
        var (_, c) = Novo();
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Post, Eventos, porta: 8080, corpo: Faixa()))).StatusCode);
    }

    // ---- token ----

    [Theory]
    [InlineData(null, "Bearer")]
    [InlineData("", "Bearer")]
    [InlineData("token-errado-token-errado-token-errado-token-errado", "Bearer")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Bearer")]   // 63: prefixo do certo
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Bearer")] // 65
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Basic")]   // esquema errado
    public async Task Na_porta_interna_sem_o_Bearer_do_playout_e_401(string? token, string esquema)
    {
        var (_, c) = Novo();

        var get = await c.SendAsync(Req(HttpMethod.Get, Proxima, token: token, esquema: esquema));
        var post = await c.SendAsync(Req(HttpMethod.Post, Eventos, token: token, esquema: esquema, corpo: Faixa()));

        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal("Bearer", get.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task JWT_de_Admin_nao_abre_as_rotas_internas()
    {
        var (f, c) = Novo(x => x.RemoteIp = IPAddress.Parse("203.0.113.40"));
        f.CriarUsuario("admin@example.com", Role.Admin);
        var login = await c.PostAsJsonAsync("/api/v1/auth/login", new { email = "admin@example.com", senha = Senha });
        var jwt = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Req(HttpMethod.Get, Proxima, token: jwt))).StatusCode);
    }

    [Fact]
    public async Task Token_anterior_e_aceito_durante_a_rotacao_e_so_enquanto_estiver_configurado()
    {
        var (_, semRotacao) = Novo();
        Assert.Equal(HttpStatusCode.Unauthorized, (await semRotacao.SendAsync(Req(HttpMethod.Get, Proxima, token: TokenAntigo))).StatusCode);

        var (_, emRotacao) = Novo(f => f.Config["Playout:TokenAnterior"] = TokenAntigo);
        Assert.Equal(HttpStatusCode.NoContent, (await emRotacao.SendAsync(Req(HttpMethod.Get, Proxima, token: TokenAntigo))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await emRotacao.SendAsync(Req(HttpMethod.Get, Proxima, token: Token))).StatusCode);
    }

    [Theory]
    [InlineData("__GERAR__")]
    [InlineData("curto")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // igual ao atual
    public void TokenAnterior_de_exemplo_curto_ou_igual_ao_atual_falha_no_boot(string anterior)
    {
        var f = new ApiFactory();
        f.Config["Playout:TokenAnterior"] = anterior;
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }

    // ---- comportamento do esqueleto ----

    [Fact]
    public async Task Proxima_responde_204_sem_faixa_e_o_bearer_e_case_insensitive_no_esquema()
    {
        var (_, c) = Novo();

        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Get, Proxima, token: Token))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Get, Proxima, token: Token, esquema: "bearer"))).StatusCode);
    }

    [Fact]
    public async Task Evento_de_faixa_responde_202_atualiza_o_tocando_agora_e_enfileira_TocandoAgora_para_todos()
    {
        var (f, c) = Novo();
        var pedidoId = Guid.NewGuid();
        var quando = new DateTime(2026, 10, 12, 18, 30, 0, DateTimeKind.Utc);

        var r = await c.SendAsync(Req(HttpMethod.Post, Eventos, token: Token, corpo: Faixa("  Garota de Ipanema ", "Tom Jobim", ocorridoEmUtc: quando, pedidoId: pedidoId)));

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var atual = await f.Services.GetRequiredService<ITocandoAgoraStore>().ObterAsync(default);
        Assert.Equal(new TocandoAgora("Garota de Ipanema", "Tom Jobim", null, pedidoId, quando), atual);

        using var escopo = f.Services.CreateScope();
        var evento = await escopo.ServiceProvider.GetRequiredService<RadioDbContext>().EventosOutbox.SingleAsync();
        Assert.Equal(EventosDoOutbox.TocandoAgora, evento.Tipo);
        var envelope = JsonDocument.Parse(evento.Payload).RootElement;
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("grupos").ValueKind); // destino explícito: todos
        Assert.Equal("Garota de Ipanema", envelope.GetProperty("dados").GetProperty("titulo").GetString());
        Assert.Equal("Tom Jobim", envelope.GetProperty("dados").GetProperty("artista").GetString());
        Assert.Equal(pedidoId, envelope.GetProperty("dados").GetProperty("pedidoId").GetGuid());
    }

    [Fact]
    public async Task Faixa_da_rotacao_local_sem_metadado_tambem_e_aceita()
    {
        var (f, c) = Novo();

        var r = await c.SendAsync(Req(HttpMethod.Post, Eventos, token: Token, corpo: new { tipo = "FaixaIniciada", ocorridoEmUtc = DateTime.UtcNow }));

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var atual = await f.Services.GetRequiredService<ITocandoAgoraStore>().ObterAsync(default);
        Assert.NotNull(atual);
        Assert.Null(atual.Titulo);
        Assert.Null(atual.MidiaId);
    }

    public static TheoryData<string, object> EventosInvalidos() => new()
    {
        { "titulo com 201 caracteres", Faixa(titulo: new string('t', 201)) },
        { "artista com 201 caracteres", Faixa(artista: new string('a', 201)) },
        { "quebra de linha no titulo", Faixa(titulo: "Faixa\nINFO linha forjada") },
        { "NUL no artista", Faixa(artista: "Banda\0") },
        { "ESC no titulo", Faixa(titulo: "\u001b[31mvermelho") },
        { "separador de linha Unicode", Faixa(titulo: "Faixa\u2028outra linha") },
        { "override de direcao (RLO)", Faixa(titulo: "Faixa\u202E3pm.exe") },
        { "isolate de direcao", Faixa(artista: "Banda\u2066x") },
        { "marca RLM", Faixa(titulo: "Faixa\u200F") },
        { "data uma hora no futuro", Faixa(ocorridoEmUtc: DateTime.UtcNow.AddHours(1)) },
        { "tipo desconhecido", Faixa(tipo: "FaixaTerminada") },
        { "tipo vazio", Faixa(tipo: "") },
        { "sem ocorridoEmUtc", new { tipo = "FaixaIniciada", titulo = "Faixa" } },
        { "pedidoId vazio", Faixa(pedidoId: Guid.Empty) },
    };

    [Theory]
    [MemberData(nameof(EventosInvalidos))]
    public async Task Evento_invalido_e_400_e_nao_publica_nada(string caso, object corpo)
    {
        var (f, c) = Novo();

        var r = await c.SendAsync(Req(HttpMethod.Post, Eventos, token: Token, corpo: corpo));

        Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"{caso}: {(int)r.StatusCode}");
        Assert.Null(await f.Services.GetRequiredService<ITocandoAgoraStore>().ObterAsync(default));
        using var escopo = f.Services.CreateScope();
        Assert.Empty(await escopo.ServiceProvider.GetRequiredService<RadioDbContext>().EventosOutbox.ToListAsync());
    }

    [Fact]
    public async Task Titulo_com_200_caracteres_acento_e_emoji_e_aceito()
    {
        var (_, c) = Novo();
        // Inclui um emoji composto com ZWJ (família): caractere de formato legítimo, não é controle de direção.
        var titulo = "Canção 🎵 👨\u200D👩 " + new string('ã', 184);
        Assert.Equal(200, titulo.Length);

        Assert.Equal(HttpStatusCode.Accepted, (await c.SendAsync(Req(HttpMethod.Post, Eventos, token: Token, corpo: Faixa(titulo: titulo)))).StatusCode);
    }

    [Fact]
    public async Task Relogio_do_playout_adiantado_dentro_da_tolerancia_e_aceito()
    {
        var (_, c) = Novo();
        Assert.Equal(HttpStatusCode.Accepted, (await c.SendAsync(Req(HttpMethod.Post, Eventos, token: Token, corpo: Faixa(ocorridoEmUtc: DateTime.UtcNow.AddMinutes(2))))).StatusCode);
    }

    [Fact]
    public async Task Corpo_que_nao_e_JSON_e_400_e_corpo_enorme_e_413()
    {
        var (_, c) = Novo();

        var lixo = await c.SendAsync(Req(HttpMethod.Post, Eventos, token: Token, corpo: new StringContent("{ isto não é json", Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.BadRequest, lixo.StatusCode);

        var enorme = new StringContent("{\"tipo\":\"FaixaIniciada\",\"titulo\":\"" + new string('x', 9000) + "\"}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await c.SendAsync(Req(HttpMethod.Post, Eventos, token: Token, corpo: enorme))).StatusCode);
    }

    [Fact]
    public async Task Polling_do_playout_na_porta_interna_nao_esbarra_no_limite_de_100_por_minuto()
    {
        var (_, c) = Novo(f => f.RemoteIp = IPAddress.Parse("172.29.0.7"));

        for (var i = 0; i < 130; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Get, Proxima, token: Token))).StatusCode);
    }
}
