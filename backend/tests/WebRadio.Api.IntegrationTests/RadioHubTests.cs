using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Api.Features.Realtime;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using Xunit;
using static WebRadio.Api.IntegrationTests.ApiFactoryExtensions;

namespace WebRadio.Api.IntegrationTests;

/// <summary>
/// E1-F10 com um cliente SignalR de verdade contra o TestServer. O outbox aqui é o banco em memória (um consumidor só); a
/// fila com UPDLOCK/READPAST é provada contra SQL Server em WebRadio.Infrastructure.Tests. O backplane e o canal de
/// desconexão entre réplicas dependem de Redis real e ficam para o CI.
/// </summary>
public class RadioHubTests
{
    private static readonly TimeSpan Prazo = TimeSpan.FromSeconds(15);

    private sealed class RefreshFalso : IRefreshTokenService
    {
        public Task<ResultadoRotacao> RotacionarAsync(string tokenBruto, byte[] ipHash, CancellationToken ct) => Task.FromResult(ResultadoRotacao.Invalido);

        public Task RevogarFamiliaAsync(string tokenBruto, CancellationToken ct) => Task.CompletedTask;

        public Task RevogarTodasDoUsuarioAsync(Guid usuarioId, string? manterFamiliaDoToken, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Cena(ApiFactory f, WebApplicationFactory<Program> host) : IAsyncDisposable
    {
        private readonly List<HubConnection> _conexoes = [];

        public ApiFactory F { get; } = f;
        public HttpClient Http { get; } = host.CreateClient(new() { BaseAddress = new Uri("http://localhost") });
        public IServiceProvider Servicos => host.Services;

        public static Cena Nova(bool dispatcher = false)
        {
            var f = new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.30") };
            if (dispatcher) f.Config["Outbox:Dispatcher"] = "true";
            return new Cena(f, f.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddScoped<IRefreshTokenService>(_ => new RefreshFalso()))));
        }

        public async Task<string> TokenDe(string email)
        {
            var r = await Http.PostAsJsonAsync("/api/v1/auth/login", new { email, senha = Senha });
            return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        }

        /// <summary>
        /// WebSocket do TestServer, sem negociação: é o caminho do navegador, com o token na QUERY (não há cabeçalho
        /// Authorization num WebSocket de navegador).
        /// </summary>
        public HubConnection Conexao(string? listenerId, string? tokenNaQuery = null)
        {
            var url = "http://localhost" + RadioHub.Rota + "?x=1"
                + (listenerId is null ? "" : "&listenerId=" + Uri.EscapeDataString(listenerId))
                + (tokenNaQuery is null ? "" : "&access_token=" + Uri.EscapeDataString(tokenNaQuery));
            var conexao = new HubConnectionBuilder().WithUrl(url, o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.SkipNegotiation = true;
                o.WebSocketFactory = async (contexto, ct) => await host.Server.CreateWebSocketClient().ConnectAsync(contexto.Uri, ct);
            }).Build();
            _conexoes.Add(conexao);
            return conexao;
        }

        /// <summary>
        /// O StartAsync do cliente volta no handshake, ANTES de o servidor terminar o OnConnectedAsync (entrada nos grupos).
        /// Publicar antes disso perderia o evento: espera o servidor dizer que as conexões estão prontas.
        /// </summary>
        public async Task Iniciar(params HubConnection[] conexoes)
        {
            var hub = Servicos.GetRequiredService<ConexoesDoHub>();
            var esperadas = hub.Prontas + conexoes.Length;
            foreach (var c in conexoes) await c.StartAsync();
            Assert.True(SpinWait.SpinUntil(() => hub.Prontas >= esperadas, Prazo), $"só {hub.Prontas} de {esperadas} conexões ficaram prontas");
        }

        public async Task Enfileirar(string tipo, object carga, params string[] grupos)
        {
            await using var escopo = Servicos.CreateAsyncScope();
            var outbox = escopo.ServiceProvider.GetRequiredService<IOutbox>();
            if (grupos.Length == 0) outbox.EnfileirarParaTodos(tipo, carga);
            else outbox.EnfileirarParaGrupos(grupos, tipo, carga);
            await escopo.ServiceProvider.GetRequiredService<RadioDbContext>().SaveChangesAsync();
        }

        public async Task<ResultadoDoLote> PasseDoOutbox()
        {
            await using var escopo = Servicos.CreateAsyncScope();
            return await escopo.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessarLoteAsync(default);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var c in _conexoes) await c.DisposeAsync();
        }
    }

    /// <summary>Registra os eventos recebidos, na ordem. "Fim" é a sentinela que os testes mandam por último, para todos.</summary>
    private sealed class Escuta
    {
        private readonly TaskCompletionSource _fim = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(string Evento, JsonElement Carga)> Recebidos { get; } = [];

        public Escuta(HubConnection conexao, params string[] eventos)
        {
            foreach (var evento in eventos)
                conexao.On<JsonElement>(evento, carga => { lock (Recebidos) Recebidos.Add((evento, carga)); });
            conexao.On<JsonElement>("Fim", _ => _fim.TrySetResult());
        }

        public Task AteOFim() => _fim.Task.WaitAsync(Prazo);

        public string[] Nomes() { lock (Recebidos) return Recebidos.Select(r => r.Evento).ToArray(); }
    }

    private static string Listener() => Guid.NewGuid().ToString();

    [Fact]
    public async Task Evento_do_outbox_chega_ao_cliente_fica_processado_e_reprocessar_nao_reenvia()
    {
        await using var cena = Cena.Nova();
        var conexao = cena.Conexao(Listener());
        var escuta = new Escuta(conexao, EventosDoOutbox.TocandoAgora);
        await cena.Iniciar(conexao);

        await cena.Enfileirar(EventosDoOutbox.TocandoAgora, new { titulo = "Faixa", artista = "Banda", tipoMidia = TipoMidia.Musica });
        await cena.Enfileirar("Fim", new { });
        Assert.Equal(new ResultadoDoLote(2, 0), await cena.PasseDoOutbox());
        await escuta.AteOFim();

        var (_, carga) = Assert.Single(escuta.Recebidos);
        Assert.Equal("Faixa", carga.GetProperty("titulo").GetString());
        Assert.Equal("Musica", carga.GetProperty("tipoMidia").GetString()); // enum como string, igual à REST
        Assert.False(string.IsNullOrEmpty(carga.GetProperty("eventoId").GetString()));
        Assert.True(carga.TryGetProperty("ocorridoEmUtc", out _));
        Assert.False(carga.TryGetProperty("grupos", out _)); // o envelope do outbox não vaza para o cliente

        await using (var escopo = cena.Servicos.CreateAsyncScope())
            Assert.All(await escopo.ServiceProvider.GetRequiredService<RadioDbContext>().EventosOutbox.ToListAsync(), e => Assert.NotNull(e.ProcessadoEmUtc));

        // Segundo passe: nada pendente, nada reenviado.
        Assert.Equal(new ResultadoDoLote(0, 0), await cena.PasseDoOutbox());
        await cena.Enfileirar("Fim", new { });
        await cena.PasseDoOutbox();
        Assert.Single(escuta.Recebidos);
    }

    [Fact]
    public async Task Com_o_dispatcher_ligado_o_evento_chega_sozinho()
    {
        await using var cena = Cena.Nova(dispatcher: true);
        var conexao = cena.Conexao(Listener());
        var escuta = new Escuta(conexao);
        await cena.Iniciar(conexao);

        await cena.Enfileirar("Fim", new { });

        await escuta.AteOFim(); // ninguém chamou o passe: quem entregou foi o BackgroundService
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nao-e-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{7b0e7c2e-1111-4222-8333-000000000001}")]
    public async Task Sem_listenerId_que_seja_um_UUID_a_conexao_e_recusada(string? listenerId)
    {
        await using var cena = Cena.Nova();
        var conexao = cena.Conexao(listenerId);
        var fechada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conexao.Closed += _ => { fechada.TrySetResult(); return Task.CompletedTask; };

        // O servidor aborta no OnConnected: ou o handshake nem termina, ou a conexão fecha logo em seguida.
        try
        {
            await conexao.StartAsync();
            await fechada.Task.WaitAsync(Prazo);
        }
        catch (Exception e) when (e is not TimeoutException)
        {
        }

        Assert.NotEqual(HubConnectionState.Connected, conexao.State);
        Assert.Equal(0, cena.Servicos.GetRequiredService<ConexoesDoHub>().Total);
    }

    [Fact]
    public async Task Grupos_sao_decididos_no_servidor_locutores_so_com_JWT_de_quem_modera_e_listener_so_o_proprio_dispositivo()
    {
        await using var cena = Cena.Nova();
        cena.F.CriarUsuario("locutor@example.com", Role.Locutor);
        cena.F.CriarUsuario("ouvinte@example.com", Role.Ouvinte);
        cena.F.CriarUsuario("admin-restrito@example.com", Role.Admin, deveTrocar: true);
        var meuListener = Guid.NewGuid();
        string[] eventos = ["PedidoCriado", "PedidoModerado"];

        var locutor = cena.Conexao(Listener(), await cena.TokenDe("locutor@example.com"));
        var ouvinteLogado = cena.Conexao(Listener(), await cena.TokenDe("ouvinte@example.com"));
        var adminRestrito = cena.Conexao(Listener(), await cena.TokenDe("admin-restrito@example.com"));
        var anonimoDono = cena.Conexao(meuListener.ToString());
        var anonimoOutro = cena.Conexao(Listener());
        var tokenLixo = cena.Conexao(Listener(), "isto.nao.e-um-jwt");
        HubConnection[] todas = [locutor, ouvinteLogado, adminRestrito, anonimoDono, anonimoOutro, tokenLixo];
        var escutas = todas.Select(c => new Escuta(c, eventos)).ToArray();
        await cena.Iniciar(todas);

        var grupoDoDono = cena.Servicos.GetRequiredService<IListenerIdentity>().GrupoListener(meuListener);
        await cena.Enfileirar("PedidoCriado", new { pedidoId = 1 }, GruposDoHub.Locutores);
        await cena.Enfileirar("PedidoModerado", new { pedidoId = 1, status = "Aprovado" }, grupoDoDono, GruposDoHub.Locutores);
        await cena.Enfileirar("Fim", new { });
        await cena.PasseDoOutbox();
        foreach (var e in escutas) await e.AteOFim();

        Assert.Equal(eventos, escutas[0].Nomes());             // locutor: os dois
        Assert.Empty(escutas[1].Nomes());                      // ouvinte logado não modera
        Assert.Empty(escutas[2].Nomes());                      // Admin com troca de senha pendente: fora de `locutores`
        Assert.Equal(["PedidoModerado"], escutas[3].Nomes());  // o dono do pedido, pelo listenerId
        Assert.Empty(escutas[4].Nomes());                      // outro dispositivo anônimo
        Assert.Empty(escutas[5].Nomes());                      // token inválido conecta como anônimo, sem grupo de locutor
    }

    [Fact]
    public async Task Mudar_o_papel_derruba_a_conexao_do_usuario_e_so_a_dele()
    {
        await using var cena = Cena.Nova();
        var locutorId = cena.F.CriarUsuario("locutor@example.com", Role.Locutor);
        cena.F.CriarUsuario("admin@example.com", Role.Admin);
        var tokenAdmin = await cena.TokenDe("admin@example.com");

        var doLocutor = cena.Conexao(Listener(), await cena.TokenDe("locutor@example.com"));
        var doAdmin = cena.Conexao(Listener(), tokenAdmin);
        var anonima = cena.Conexao(Listener());
        var caiu = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        doLocutor.Closed += _ => { caiu.TrySetResult(); return Task.CompletedTask; };
        await cena.Iniciar(doLocutor, doAdmin, anonima);
        Assert.Equal(2, cena.Servicos.GetRequiredService<ConexoesDoHub>().Total); // só as autenticadas são rastreadas

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/usuarios/{locutorId}") { Content = JsonContent.Create(new { role = "Ouvinte" }) };
        patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenAdmin);
        patch.Headers.TryAddWithoutValidation("If-Match", "\"\"");
        Assert.Equal(HttpStatusCode.OK, (await cena.Http.SendAsync(patch)).StatusCode);
        // Até o outbox rodar, nada cai: o handler não fala com o hub (D22).
        Assert.Equal(HubConnectionState.Connected, doLocutor.State);

        Assert.Equal(new ResultadoDoLote(1, 0), await cena.PasseDoOutbox());

        await caiu.Task.WaitAsync(Prazo);
        Assert.Equal(HubConnectionState.Connected, doAdmin.State);
        Assert.Equal(HubConnectionState.Connected, anonima.State);
        Assert.Equal(1, cena.Servicos.GetRequiredService<ConexoesDoHub>().Total);
    }

    [Fact]
    public async Task Token_na_query_so_vale_no_hub_nunca_na_REST()
    {
        await using var cena = Cena.Nova();
        cena.F.CriarUsuario("locutor@example.com", Role.Locutor);
        var token = await cena.TokenDe("locutor@example.com");

        var r = await cena.Http.GetAsync("/api/v1/auth/me?access_token=" + Uri.EscapeDataString(token));

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task O_cliente_nao_tem_metodo_para_chamar_no_servidor()
    {
        await using var cena = Cena.Nova();
        var conexao = cena.Conexao(Listener());
        await cena.Iniciar(conexao);

        // Entrar num grupo por conta própria, por exemplo, não existe.
        await Assert.ThrowsAnyAsync<Exception>(() => conexao.InvokeAsync("AddToGroup", GruposDoHub.Locutores));
        await Assert.ThrowsAnyAsync<Exception>(() => conexao.InvokeAsync("OnConnectedAsync"));
    }
}
