using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Transmissao;
using Xunit;

namespace WebRadio.Api.IntegrationTests;

/// <summary>
/// E2-F05 no nível HTTP. A janela em SQL (SYSUTCDATETIME) é provada em WebRadio.Infrastructure.Tests; aqui as consultas são falsas
/// (o banco é InMemory) e o resto da composição é a real: listener :8081, Bearer do playout, throttle, logs.
/// </summary>
[Collection(ColecaoDeLogs.Nome)]
public class HarborEndpointsTests
{
    private const string Auth = "/api/v1/internal/harbor/auth";
    private static readonly string Token = new('a', 64);

    private sealed class Consultas : IConsultasDoHarbor
    {
        public Dictionary<Guid, CredencialDoHarbor> Credenciais { get; } = [];
        public Dictionary<Guid, JanelaDoHarbor> Janelas { get; } = [];

        public Task<CredencialDoHarbor?> CredencialAtivaAsync(Guid identificador, CancellationToken ct) => Task.FromResult(Credenciais.GetValueOrDefault(identificador));

        public Task<JanelaDoHarbor?> JanelaAsync(Guid identificador, CancellationToken ct) =>
            Task.FromResult(Janelas.TryGetValue(identificador, out var j) ? j : null);

        public Task RegistrarUsoAsync(Guid credencialId, CancellationToken ct) => Task.CompletedTask;
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

    private sealed record Cena(HttpClient C, Consultas Consultas, Captura Log, Guid Id, string Segredo, JanelaDoHarbor Janela);

    private static Cena NovaCena(bool atrasDoProxy = false)
    {
        var f = new ApiFactory();
        f.Config["Harbor:AtrasDoProxy"] = atrasDoProxy ? "true" : "false";
        var consultas = new Consultas();
        var log = new Captura();
        var host = f.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddScoped<IConsultasDoHarbor>(_ => consultas);
            s.AddSingleton<ILogEventSink>(log);
        }));
        var id = SegredoDeTransmissao.NovoIdentificador();
        var g = SegredoDeTransmissao.Gerar();
        consultas.Credenciais[id] = new CredencialDoHarbor(Guid.NewGuid(), g.Salt, g.Hash);
        var janela = new JanelaDoHarbor(Guid.NewGuid(), new DateTime(2026, 10, 16, 20, 0, 0, DateTimeKind.Utc));
        consultas.Janelas[id] = janela;
        return new Cena(host.CreateClient(new() { BaseAddress = new Uri("http://localhost") }), consultas, log, id, g.Segredo, janela);
    }

    private static HttpRequestMessage Req(HttpMethod metodo, string url, object? corpo = null, int porta = 8081, string? token = null)
    {
        var req = new HttpRequestMessage(metodo, url);
        req.Headers.Add("X-Test-LocalPort", porta.ToString(System.Globalization.CultureInfo.InvariantCulture));
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token ?? Token}");
        if (corpo is not null) req.Content = JsonContent.Create(corpo);
        return req;
    }

    private static Task<HttpResponseMessage> Autenticar(Cena c, string? usuario, string? senha, string ip = "203.0.113.77") =>
        c.C.SendAsync(Req(HttpMethod.Post, Auth, new { usuario, senha, enderecoIp = ip }));

    [Fact]
    public async Task Credencial_certa_na_janela_devolve_200_com_programa_e_fim_utc()
    {
        var c = NovaCena();

        var r = await Autenticar(c, c.Id.ToString(), c.Segredo);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var corpo = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(c.Janela.ProgramaId, corpo.GetProperty("programaId").GetGuid());
        Assert.Equal("2026-10-16T20:00:00Z", corpo.GetProperty("fimUtc").GetString());
    }

    [Fact]
    public async Task Todo_motivo_de_recusa_e_o_mesmo_401_sem_corpo()
    {
        var c = NovaCena();
        var foraDaJanela = SegredoDeTransmissao.NovoIdentificador();
        var g = SegredoDeTransmissao.Gerar();
        c.Consultas.Credenciais[foraDaJanela] = new CredencialDoHarbor(Guid.NewGuid(), g.Salt, g.Hash);

        var respostas = new[]
        {
            await Autenticar(c, SegredoDeTransmissao.NovoIdentificador().ToString(), c.Segredo), // inexistente
            await Autenticar(c, c.Id.ToString(), SegredoDeTransmissao.Gerar().Segredo),            // segredo errado
            await Autenticar(c, foraDaJanela.ToString(), g.Segredo),                                 // fora da janela
            await Autenticar(c, "nao-e-guid", c.Segredo),                                             // formato
            await Autenticar(c, c.Id.ToString().ToUpperInvariant(), c.Segredo),
            await Autenticar(c, "../" + c.Id, c.Segredo),
            await Autenticar(c, c.Id + "\n", c.Segredo),
            await Autenticar(c, "\"" + c.Id + "\"", c.Segredo),
            await Autenticar(c, c.Id.ToString(), c.Segredo + "="),
            await Autenticar(c, c.Id.ToString(), c.Segredo, ip: "nao-e-ip"),
        };

        foreach (var r in respostas)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal("", await r.Content.ReadAsStringAsync());
            Assert.Null(r.Content.Headers.ContentType);
            Assert.Empty(r.Headers.WwwAuthenticate);
        }
    }

    [Fact]
    public async Task Segredo_mandado_no_campo_usuario_nao_aparece_em_nenhum_log()
    {
        var c = NovaCena();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Autenticar(c, c.Segredo, c.Segredo)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Autenticar(c, c.Id.ToString(), c.Segredo)).StatusCode);

        var logs = c.Log.Tudo();
        Assert.Contains("usuario_invalido", logs);
        Assert.Contains("HARBOR_AUTH_OK", logs);
        Assert.DoesNotContain(c.Segredo, logs);
        Assert.DoesNotContain("203.0.113.77", logs);
    }

    [Fact]
    public async Task Par_bloqueia_em_modo_direto_e_atras_do_proxy_11_falhas_nao_bloqueiam_a_credencial_certa()
    {
        foreach (var proxy in new[] { false, true })
        {
            var c = NovaCena(proxy);
            for (var i = 0; i < 11; i++) await Autenticar(c, c.Id.ToString(), SegredoDeTransmissao.Gerar().Segredo);

            var r = await Autenticar(c, c.Id.ToString(), c.Segredo);

            Assert.Equal(proxy ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, r.StatusCode);
        }
    }

    [Fact]
    public async Task Autorizacao_200_na_janela_e_403_sem_corpo_fora_dela_ou_com_identificador_invalido()
    {
        var c = NovaCena();

        var ok = await c.C.SendAsync(Req(HttpMethod.Get, $"/api/v1/internal/harbor/autorizacao/{c.Id}"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(c.Janela.ProgramaId, (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("programaId").GetGuid());

        c.Consultas.Janelas.Clear();
        foreach (var id in new[] { c.Id.ToString(), "nao-e-guid", c.Id.ToString().ToUpperInvariant() })
        {
            var r = await c.C.SendAsync(Req(HttpMethod.Get, $"/api/v1/internal/harbor/autorizacao/{id}"));
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("", await r.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("POST", "/api/v1/internal/harbor/auth")]
    [InlineData("GET", "/api/v1/internal/harbor/autorizacao/3f2b1c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d")]
    [InlineData("POST", "/api/v1/INTERNAL/harbor/auth")]
    [InlineData("POST", "/api/v1//internal/harbor/auth")]
    public async Task Pela_porta_publica_as_rotas_do_harbor_sao_404_mesmo_com_o_token(string metodo, string caminho)
    {
        var c = NovaCena();

        var r = await c.C.SendAsync(Req(new HttpMethod(metodo), caminho, metodo == "POST" ? new { usuario = c.Id, senha = c.Segredo, enderecoIp = "203.0.113.1" } : null, porta: 8080));

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Sem_o_Bearer_do_playout_e_401_com_WWW_Authenticate()
    {
        var c = NovaCena();

        var r = await c.C.SendAsync(Req(HttpMethod.Post, Auth, new { usuario = c.Id, senha = c.Segredo, enderecoIp = "203.0.113.1" }, token: "errado"));

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("Bearer", r.Headers.WwwAuthenticate.ToString());
    }
}
