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
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using WebRadio.Infrastructure.Seguranca;
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
    public async Task Trocar_a_senha_derruba_as_conexoes_do_hub_abertas_com_o_token_antigo()
    {
        await using var cena = Cena.Nova();
        cena.F.CriarUsuario("locutor@example.com", Role.Locutor);
        cena.F.CriarUsuario("outro@example.com", Role.Locutor);
        var token = await cena.TokenDe("locutor@example.com");

        // Duas abas (ou a aba do dono e a de quem roubou o token) e um colega que não tem nada com isso.
        var aba1 = cena.Conexao(Listener(), token);
        var aba2 = cena.Conexao(Listener(), token);
        var doColega = cena.Conexao(Listener(), await cena.TokenDe("outro@example.com"));
        var cairam = new[] { aba1, aba2 }.Select(c =>
        {
            var caiu = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            c.Closed += _ => { caiu.TrySetResult(); return Task.CompletedTask; };
            return caiu.Task;
        }).ToArray();
        await cena.Iniciar(aba1, aba2, doColega);

        var troca = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/trocar-senha") { Content = JsonContent.Create(new { senhaAtual = Senha, novaSenha = "outra-senha-bem-longa-456" }) };
        troca.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.NoContent, (await cena.Http.SendAsync(troca)).StatusCode);

        Assert.Equal(new ResultadoDoLote(1, 0), await cena.PasseDoOutbox());

        await Task.WhenAll(cairam).WaitAsync(Prazo);
        Assert.Equal(HubConnectionState.Connected, doColega.State);
        Assert.Equal(1, cena.Servicos.GetRequiredService<ConexoesDoHub>().Total);
    }

    private static readonly string[] SoOAlerta = [EventosDoOutbox.AlertaHarborFalhas];

    /// <summary>
    /// Envia o par de alertas da E2-F12 (credencial do dono e global) e roda o passe do outbox. A sentinela "Fim" libera o
    /// <c>AteOFim</c> na PRIMEIRA que chega: quem manda mais eventos depois passa <c>comFim: false</c> e manda a sua, uma só, no fim.
    /// </summary>
    private static async Task EnviarAlertas(Cena cena, Guid donoId, bool comFim = true)
    {
        await cena.Enfileirar(EventosDoOutbox.AlertaHarborFalhas,
            new { tipo = "credencial", falhas = 10, janelaMinutos = 5, locutorId = donoId, locutorNome = "Fulano", credencialAtiva = true },
            GruposDoHub.Admins, GruposDoHub.Usuario(donoId));
        await cena.Enfileirar(EventosDoOutbox.AlertaHarborFalhas, new { tipo = "global", falhas = 50, janelaMinutos = 5 }, GruposDoHub.Admins);
        if (comFim) await cena.Enfileirar("Fim", new { });
        await cena.PasseDoOutbox();
    }

    private static string[] Tipos(Escuta e) { lock (e.Recebidos) return e.Recebidos.Select(r => r.Carga.GetProperty("tipo").GetString()!).ToArray(); }

    [Fact]
    public async Task AlertaHarborFalhas_so_chega_a_admins_e_ao_dono_nunca_a_outro_locutor_nem_ao_token_restrito()
    {
        await using var cena = Cena.Nova();
        var donoId = cena.F.CriarUsuario("dono@example.com", Role.Locutor);
        cena.F.CriarUsuario("outro@example.com", Role.Locutor);
        cena.F.CriarUsuario("admin@example.com", Role.Admin);
        cena.F.CriarUsuario("admin-restrito@example.com", Role.Admin, deveTrocar: true);
        var donoRestritoId = cena.F.CriarUsuario("dono-restrito@example.com", Role.Locutor, deveTrocar: true);

        var dono = cena.Conexao(Listener(), await cena.TokenDe("dono@example.com"));
        var outro = cena.Conexao(Listener(), await cena.TokenDe("outro@example.com"));
        var admin = cena.Conexao(Listener(), await cena.TokenDe("admin@example.com"));
        var adminRestrito = cena.Conexao(Listener(), await cena.TokenDe("admin-restrito@example.com"));
        var donoRestrito = cena.Conexao(Listener(), await cena.TokenDe("dono-restrito@example.com"));
        var anonimo = cena.Conexao(Listener());
        HubConnection[] todas = [dono, outro, admin, adminRestrito, donoRestrito, anonimo];
        var escutas = todas.Select(c => new Escuta(c, SoOAlerta)).ToArray();
        await cena.Iniciar(todas);

        await EnviarAlertas(cena, donoId, comFim: false);
        // O dono com token restrito também seria destinatário do grupo dele: o evento do SEU id não chega por causa do token.
        await cena.Enfileirar(EventosDoOutbox.AlertaHarborFalhas, new { tipo = "credencial", falhas = 10, janelaMinutos = 5, locutorId = donoRestritoId, locutorNome = "Fulano", credencialAtiva = false },
            GruposDoHub.Admins, GruposDoHub.Usuario(donoRestritoId));
        await cena.Enfileirar("Fim", new { });
        await cena.PasseDoOutbox();
        foreach (var e in escutas) await e.AteOFim();

        Assert.Equal(["credencial"], Tipos(escutas[0]));                  // o dono: só o alerta da credencial dele
        Assert.Empty(escutas[1].Nomes());                                 // outro locutor: nada
        Assert.Equal(["credencial", "global", "credencial"], Tipos(escutas[2])); // Admin: tudo
        Assert.Empty(escutas[3].Nomes());                                 // Admin com token restrito: nada
        Assert.Empty(escutas[4].Nomes());                                 // dono com token restrito: nada
        Assert.Empty(escutas[5].Nomes());                                 // anônimo: nada
    }

    [Fact]
    public async Task Grupo_sem_nenhuma_conexao_junto_de_admins_nao_impede_a_entrega_ao_Admin()
    {
        // O dono da credencial offline: o grupo usuario:{dono} não existe, e o Admin recebe do mesmo jeito (o alerta não depende do dono).
        await using var cena = Cena.Nova();
        cena.F.CriarUsuario("admin@example.com", Role.Admin);
        var admin = cena.Conexao(Listener(), await cena.TokenDe("admin@example.com"));
        var escuta = new Escuta(admin, SoOAlerta);
        await cena.Iniciar(admin);

        await cena.Enfileirar(EventosDoOutbox.AlertaHarborFalhas, new { tipo = "credencial", falhas = 10, janelaMinutos = 5, locutorId = Guid.NewGuid(), locutorNome = "Fulano", credencialAtiva = true },
            GruposDoHub.Admins, GruposDoHub.Usuario(Guid.NewGuid()));
        await cena.Enfileirar("Fim", new { });
        var lote = await cena.PasseDoOutbox();
        await escuta.AteOFim();

        Assert.Equal(new ResultadoDoLote(2, 0), lote); // nenhuma falha de entrega (OUTBOX_FALHA)
        Assert.Equal(["credencial"], Tipos(escuta));
    }

    [Theory]
    [InlineData("role")]
    [InlineData("ativo")]
    public async Task Admin_rebaixado_ou_desativado_perde_a_conexao_e_nao_recebe_mais_o_AlertaHarborFalhas(string mudanca)
    {
        await using var cena = Cena.Nova();
        var donoId = cena.F.CriarUsuario("dono@example.com", Role.Locutor);
        var alvoId = cena.F.CriarUsuario("alvo@example.com", Role.Admin);
        cena.F.CriarUsuario("admin@example.com", Role.Admin);
        var tokenAlvo = await cena.TokenDe("alvo@example.com");
        var tokenAdmin = await cena.TokenDe("admin@example.com");

        var doAlvo = cena.Conexao(Listener(), tokenAlvo);
        var doAdmin = cena.Conexao(Listener(), tokenAdmin);
        var caiu = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        doAlvo.Closed += _ => { caiu.TrySetResult(); return Task.CompletedTask; };
        var escutaAdmin = new Escuta(doAdmin, SoOAlerta);
        await cena.Iniciar(doAlvo, doAdmin);

        object corpo = mudanca == "role" ? new { role = "Ouvinte" } : new { ativo = false };
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/usuarios/{alvoId}") { Content = JsonContent.Create(corpo) };
        patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenAdmin);
        patch.Headers.TryAddWithoutValidation("If-Match", "\"\"");
        Assert.Equal(HttpStatusCode.OK, (await cena.Http.SendAsync(patch)).StatusCode);
        await cena.PasseDoOutbox();
        await caiu.Task.WaitAsync(Prazo);

        // O Closed do cliente volta antes de o servidor tirar a conexão da contagem: espera, senão o Iniciar contaria a que caiu.
        Assert.True(SpinWait.SpinUntil(() => cena.Servicos.GetRequiredService<ConexoesDoHub>().Prontas == 1, Prazo));

        // Quem tenta voltar com o token antigo cai na checagem da `sv`: entra como anônimo, sem `admins`.
        var volta = cena.Conexao(Listener(), tokenAlvo);
        var escutaVolta = new Escuta(volta, SoOAlerta);
        await cena.Iniciar(volta);

        await EnviarAlertas(cena, donoId);
        await escutaAdmin.AteOFim();
        await escutaVolta.AteOFim();

        Assert.Equal(["credencial", "global"], Tipos(escutaAdmin));
        Assert.Empty(escutaVolta.Nomes());
        Assert.Equal(HubConnectionState.Disconnected, doAlvo.State);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("ativo")]
    public async Task Locutor_rebaixado_ou_desativado_perde_a_conexao_e_nao_recebe_mais_o_PedidoCriado(string mudanca)
    {
        await using var cena = Cena.Nova();
        var alvoId = cena.F.CriarUsuario("locutor-alvo@example.com", Role.Locutor);
        cena.F.CriarUsuario("locutor-fica@example.com", Role.Locutor);
        cena.F.CriarUsuario("admin@example.com", Role.Admin);
        var tokenAlvo = await cena.TokenDe("locutor-alvo@example.com");
        var tokenAdmin = await cena.TokenDe("admin@example.com");
        var tokenFica = await cena.TokenDe("locutor-fica@example.com");

        var doAlvo = cena.Conexao(Listener(), tokenAlvo);
        var doQueFica = cena.Conexao(Listener(), tokenFica);
        var caiu = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        doAlvo.Closed += _ => { caiu.TrySetResult(); return Task.CompletedTask; };
        var escutaFica = new Escuta(doQueFica, EventosDoOutbox.PedidoCriado);
        await cena.Iniciar(doAlvo, doQueFica);

        object corpo = mudanca == "role" ? new { role = "Ouvinte" } : new { ativo = false };
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/usuarios/{alvoId}") { Content = JsonContent.Create(corpo) };
        patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenAdmin);
        patch.Headers.TryAddWithoutValidation("If-Match", "\"\"");
        Assert.Equal(HttpStatusCode.OK, (await cena.Http.SendAsync(patch)).StatusCode);
        await cena.PasseDoOutbox();
        await caiu.Task.WaitAsync(Prazo);
        Assert.True(SpinWait.SpinUntil(() => cena.Servicos.GetRequiredService<ConexoesDoHub>().Prontas == 1, Prazo));

        // Quem tenta voltar com o token antigo cai na checagem da `sv`: entra como anônimo, sem o grupo `locutores`.
        var volta = cena.Conexao(Listener(), tokenAlvo);
        var escutaVolta = new Escuta(volta, EventosDoOutbox.PedidoCriado);
        await cena.Iniciar(volta);

        // PedidoCriado leva nome e dedicatória de ouvinte: só o locutor que continua locutor recebe.
        await cena.Enfileirar(EventosDoOutbox.PedidoCriado, new { id = Guid.NewGuid(), nomeOuvinte = "Maria", status = "Pendente" }, GruposDoHub.Locutores);
        await cena.Enfileirar("Fim", new { });
        await cena.PasseDoOutbox();
        await escutaFica.AteOFim();
        await escutaVolta.AteOFim();

        Assert.Equal([EventosDoOutbox.PedidoCriado], escutaFica.Nomes());
        Assert.Empty(escutaVolta.Nomes());
        Assert.Equal(HubConnectionState.Disconnected, doAlvo.State);
    }

    [Fact]
    public async Task Admin_com_token_vencido_nao_recebe_o_AlertaHarborFalhas_nem_ao_ficar_conectado_nem_ao_reconectar()
    {
        await using var cena = Cena.Nova();
        var donoId = cena.F.CriarUsuario("dono@example.com", Role.Locutor);
        var adminId = cena.F.CriarUsuario("admin@example.com", Role.Admin);
        Usuario admin;
        await using (var escopo = cena.Servicos.CreateAsyncScope())
            admin = await escopo.ServiceProvider.GetRequiredService<RadioDbContext>().Usuarios.AsNoTracking().SingleAsync(u => u.Id == adminId);

        // Tokens emitidos com o relógio atrasado: o que vence em ~3 s (conectado, o hub fecha na hora do vencimento) e o que já
        // venceu há 2 min (além da tolerância de 30 s: a conexão entra como anônima).
        var chave = System.Buffers.Text.Base64Url.DecodeFromChars(ApiFactory.ChaveValida);
        string Emitir(TimeSpan faltaParaVencer) =>
            new EmissorDeTokens(chave, new RelogioFixo(DateTime.UtcNow - EmissorDeTokens.DuracaoDoAccess + faltaParaVencer)).EmitirAccessToken(admin).Token;

        var vencendo = cena.Conexao(Listener(), Emitir(TimeSpan.FromSeconds(3)));
        var jaVencido = cena.Conexao(Listener(), Emitir(TimeSpan.FromMinutes(-2)));
        var caiu = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vencendo.Closed += _ => { caiu.TrySetResult(); return Task.CompletedTask; };
        var escutaVencendo = new Escuta(vencendo, SoOAlerta);
        var escutaVencido = new Escuta(jaVencido, SoOAlerta);
        var vivo = cena.Conexao(Listener(), await cena.TokenDe("admin@example.com"));
        var escutaVivo = new Escuta(vivo, SoOAlerta);
        await cena.Iniciar(vencendo, jaVencido, vivo);

        await caiu.Task.WaitAsync(Prazo); // o token venceu: o hub encerrou a conexão
        await EnviarAlertas(cena, donoId);
        await escutaVivo.AteOFim();

        Assert.Equal(["credencial", "global"], Tipos(escutaVivo)); // controle: o Admin com token válido recebe
        Assert.Empty(escutaVencendo.Nomes());
        Assert.Empty(escutaVencido.Nomes());
    }

    private sealed class RelogioFixo(DateTime agora) : IClock
    {
        public DateTime UtcNow => agora;
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
