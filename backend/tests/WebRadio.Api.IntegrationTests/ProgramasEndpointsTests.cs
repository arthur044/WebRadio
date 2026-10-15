using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Domain.Enums;
using WebRadio.Infrastructure.Persistencia;
using Xunit;
using static WebRadio.Api.IntegrationTests.ApiFactoryExtensions;

namespace WebRadio.Api.IntegrationTests;

/// <summary>
/// E2-F02: <c>/programas</c> no nível HTTP (02 §4 e a matriz RBAC do §1), com banco em memória. O RowVersion não existe em memória
/// (ETag vazio) e o lock de grade só tem efeito no SQL Server: os dois são provados em WebRadio.Infrastructure.Tests.
/// </summary>
public class ProgramasEndpointsTests
{
    private const string ETagVazio = "\"\"";
    private const string Rota = "/api/v1/programas";

    private sealed record Cena(ApiFactory F, HttpClient C, Guid AdminId, Guid LocutorId, Guid OutroLocutorId, Guid OuvinteId);

    private static Cena NovaCena()
    {
        var f = new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.30") };
        var admin = f.CriarUsuario("admin@example.com", Role.Admin);
        var locutor = f.CriarUsuario("locutor@example.com", Role.Locutor);
        var outro = f.CriarUsuario("outro@example.com", Role.Locutor);
        var ouvinte = f.CriarUsuario("ouvinte@example.com", Role.Ouvinte);
        return new Cena(f, f.CreateClient(new() { BaseAddress = new Uri("http://localhost") }), admin, locutor, outro, ouvinte);
    }

    private static async Task<string> TokenDe(HttpClient c, string email)
    {
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new { email, senha = Senha });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Enviar(HttpClient c, HttpMethod metodo, string url, string? token = null, object? corpo = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(metodo, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (corpo is not null) req.Content = JsonContent.Create(corpo);
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await c.SendAsync(req);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss'Z'");

    private static DateTime Agora() => new(DateTime.UtcNow.Ticks - (DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    private static object Corpo(Guid locutorId, DateTime inicio, DateTime fim, string titulo = "Manhã Fresca") =>
        new { titulo, descricao = "desc", locutorId, inicioUtc = Iso(inicio), fimUtc = Iso(fim) };

    private static async Task<string> CriarComoAdmin(Cena c, DateTime inicio, DateTime fim, Guid? locutor = null, string titulo = "Programa")
    {
        var r = await Enviar(c.C, HttpMethod.Post, Rota, await TokenDe(c.C, "admin@example.com"), Corpo(locutor ?? c.LocutorId, inicio, fim, titulo));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetString()!;
    }

    // ---- RBAC ----

    [Fact]
    public async Task Anonimo_so_le_a_grade_o_ao_vivo_e_o_programa_por_id()
    {
        var c = NovaCena();
        var inicio = Agora().AddHours(2);
        var id = await CriarComoAdmin(c, inicio, inicio.AddHours(1));

        Assert.Equal(HttpStatusCode.OK, (await Enviar(c.C, HttpMethod.Get, $"{Rota}?deUtc={Iso(inicio.AddHours(-1))}&ateUtc={Iso(inicio.AddHours(3))}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Enviar(c.C, HttpMethod.Get, $"{Rota}/ao-vivo")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Enviar(c.C, HttpMethod.Get, $"{Rota}/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Enviar(c.C, HttpMethod.Post, Rota, corpo: Corpo(c.LocutorId, inicio.AddHours(5), inicio.AddHours(6)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Enviar(c.C, HttpMethod.Put, $"{Rota}/{id}", corpo: Corpo(c.LocutorId, inicio, inicio.AddHours(1)), ifMatch: ETagVazio)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/cancelar")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/encerrar")).StatusCode);
    }

    [Theory]
    [InlineData("locutor@example.com")]
    [InlineData("ouvinte@example.com")]
    public async Task Criar_editar_e_cancelar_sao_so_do_Admin(string email)
    {
        var c = NovaCena();
        var inicio = Agora().AddHours(2);
        var id = await CriarComoAdmin(c, inicio, inicio.AddHours(1));
        var token = await TokenDe(c.C, email);

        Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(c.C, HttpMethod.Post, Rota, token, Corpo(c.LocutorId, inicio.AddHours(5), inicio.AddHours(6)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(c.C, HttpMethod.Put, $"{Rota}/{id}", token, Corpo(c.LocutorId, inicio, inicio.AddHours(1)), ETagVazio)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/cancelar", token)).StatusCode);
    }

    [Fact]
    public async Task Ouvinte_nao_encerra_programa_nenhum()
    {
        var c = NovaCena();
        var id = await CriarComoAdmin(c, Agora(), Agora().AddHours(1));

        Assert.Equal(HttpStatusCode.Forbidden, (await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/encerrar", await TokenDe(c.C, "ouvinte@example.com"))).StatusCode);
    }

    [Fact]
    public async Task Locutor_encerra_so_o_proprio_e_o_Admin_encerra_qualquer_um()
    {
        var c = NovaCena();
        var dono = await CriarComoAdmin(c, Agora().AddSeconds(-30), Agora().AddHours(1), c.LocutorId, "Do locutor");
        var token = await TokenDe(c.C, "locutor@example.com");
        var tokenOutro = await TokenDe(c.C, "outro@example.com");

        var alheio = await Enviar(c.C, HttpMethod.Post, $"{Rota}/{dono}/encerrar", tokenOutro);
        Assert.Equal(HttpStatusCode.Forbidden, alheio.StatusCode);
        Assert.Contains("/erros/acesso-negado", await alheio.Content.ReadAsStringAsync());

        var proprio = await Enviar(c.C, HttpMethod.Post, $"{Rota}/{dono}/encerrar", token);
        Assert.Equal(HttpStatusCode.OK, proprio.StatusCode);
        Assert.Equal("Finalizado", (await Json(proprio)).GetProperty("status").GetString());

        var outroPrograma = await CriarComoAdmin(c, Agora().AddSeconds(-20), Agora().AddHours(1), c.OutroLocutorId, "Do outro");
        Assert.Equal(HttpStatusCode.OK, (await Enviar(c.C, HttpMethod.Post, $"{Rota}/{outroPrograma}/encerrar", await TokenDe(c.C, "admin@example.com"))).StatusCode);
    }

    [Fact]
    public async Task Admin_com_troca_de_senha_pendente_nao_cria_programa()
    {
        var c = NovaCena();
        c.F.CriarUsuario("semeado@example.com", Role.Admin, deveTrocar: true);

        var r = await Enviar(c.C, HttpMethod.Post, Rota, await TokenDe(c.C, "semeado@example.com"), Corpo(c.LocutorId, Agora().AddHours(1), Agora().AddHours(2)));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("/erros/troca-de-senha-obrigatoria", await r.Content.ReadAsStringAsync());
    }

    // ---- POST ----

    [Fact]
    public async Task Criar_devolve_201_com_Location_ETag_e_o_ProgramaDto_sem_o_campo_de_versao()
    {
        var c = NovaCena();
        var inicio = Agora().AddHours(2);

        var r = await Enviar(c.C, HttpMethod.Post, Rota, await TokenDe(c.C, "admin@example.com"), Corpo(c.LocutorId, inicio, inicio.AddHours(1)));

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var json = await Json(r);
        var id = json.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/programas/{id}", r.Headers.Location?.OriginalString);
        Assert.NotNull(r.Headers.ETag);
        Assert.Equal("Agendado", json.GetProperty("status").GetString());
        Assert.Equal(c.LocutorId, json.GetProperty("locutor").GetProperty("id").GetGuid());
        Assert.Equal("Fulano", json.GetProperty("locutor").GetProperty("nome").GetString());
        Assert.Equal(Iso(inicio), json.GetProperty("inicioUtc").GetDateTime().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'"));
        Assert.False(json.TryGetProperty("versao", out _)); // o contrato do 02 não tem o campo; só o ETag
    }

    [Fact]
    public async Task Conflito_de_grade_e_409_com_o_id_do_programa_conflitante()
    {
        var c = NovaCena();
        var inicio = Agora().AddHours(2);
        var existente = await CriarComoAdmin(c, inicio, inicio.AddHours(2));

        var r = await Enviar(c.C, HttpMethod.Post, Rota, await TokenDe(c.C, "admin@example.com"), Corpo(c.OutroLocutorId, inicio.AddHours(1), inicio.AddHours(3)));

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var json = await Json(r);
        Assert.Equal("/erros/conflito-grade", json.GetProperty("type").GetString());
        Assert.Equal(existente, json.GetProperty("programaConflitanteId").GetString());
    }

    [Fact]
    public async Task Programas_encostados_nao_conflitam()
    {
        var c = NovaCena();
        var inicio = Agora().AddHours(2);
        await CriarComoAdmin(c, inicio, inicio.AddHours(1));

        await CriarComoAdmin(c, inicio.AddHours(1), inicio.AddHours(2)); // 201: [a, b) e [b, c)
    }

    [Theory]
    [InlineData("fim-antes-do-inicio")]
    [InlineData("duracao-curta")]
    [InlineData("duracao-longa")]
    [InlineData("inicio-no-passado")]
    [InlineData("offset-nao-zero")]
    [InlineData("sem-offset")]
    [InlineData("locutor-inexistente")]
    [InlineData("ouvinte-como-locutor")]
    [InlineData("titulo-vazio")]
    public async Task Corpo_invalido_e_400_de_validacao(string caso)
    {
        var c = NovaCena();
        var i = Agora().AddHours(2);
        object corpo = caso switch
        {
            "fim-antes-do-inicio" => Corpo(c.LocutorId, i, i.AddHours(-1)),
            "duracao-curta" => Corpo(c.LocutorId, i, i.AddMinutes(4)),
            "duracao-longa" => Corpo(c.LocutorId, i, i.AddHours(13)),
            "inicio-no-passado" => Corpo(c.LocutorId, Agora().AddMinutes(-5), Agora().AddHours(1)),
            "offset-nao-zero" => new { titulo = "X", locutorId = c.LocutorId, inicioUtc = "2030-10-20T10:00:00-03:00", fimUtc = "2030-10-20T11:00:00-03:00" },
            "sem-offset" => new { titulo = "X", locutorId = c.LocutorId, inicioUtc = "2030-10-20T10:00:00", fimUtc = "2030-10-20T11:00:00" },
            "locutor-inexistente" => Corpo(Guid.NewGuid(), i, i.AddHours(1)),
            "ouvinte-como-locutor" => Corpo(c.OuvinteId, i, i.AddHours(1)),
            _ => Corpo(c.LocutorId, i, i.AddHours(1), titulo: "  "),
        };

        var r = await Enviar(c.C, HttpMethod.Post, Rota, await TokenDe(c.C, "admin@example.com"), corpo);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("/erros/validacao", (await Json(r)).GetProperty("type").GetString());
    }

    // ---- PUT ----

    [Fact]
    public async Task Editar_exige_If_Match_428_versao_ilegivel_ou_velha_412_e_com_o_ETag_certo_200()
    {
        var c = NovaCena();
        var token = await TokenDe(c.C, "admin@example.com");
        var inicio = Agora().AddHours(2);
        var id = await CriarComoAdmin(c, inicio, inicio.AddHours(1));
        var corpo = Corpo(c.OutroLocutorId, inicio, inicio.AddHours(2), "Novo título");

        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Enviar(c.C, HttpMethod.Put, $"{Rota}/{id}", token, corpo)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Enviar(c.C, HttpMethod.Put, $"{Rota}/{id}", token, corpo, "W/\"abc\"")).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Enviar(c.C, HttpMethod.Put, $"{Rota}/{id}", token, corpo, "\"AQID\"")).StatusCode); // versão que não é a atual

        var ok = await Enviar(c.C, HttpMethod.Put, $"{Rota}/{id}", token, corpo, ETagVazio);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Novo título", (await Json(ok)).GetProperty("titulo").GetString());
    }

    [Fact]
    public async Task Editar_para_cruzar_outro_programa_e_409_e_so_Agendado_edita()
    {
        var c = NovaCena();
        var token = await TokenDe(c.C, "admin@example.com");
        var inicio = Agora().AddHours(2);
        var a = await CriarComoAdmin(c, inicio, inicio.AddHours(1));
        var b = await CriarComoAdmin(c, inicio.AddHours(3), inicio.AddHours(4));

        var conflito = await Enviar(c.C, HttpMethod.Put, $"{Rota}/{b}", token, Corpo(c.LocutorId, inicio.AddMinutes(30), inicio.AddHours(4)), ETagVazio);
        Assert.Equal(HttpStatusCode.Conflict, conflito.StatusCode);
        Assert.Equal(a, (await Json(conflito)).GetProperty("programaConflitanteId").GetString());

        await Enviar(c.C, HttpMethod.Post, $"{Rota}/{a}/cancelar", token);
        var cancelado = await Enviar(c.C, HttpMethod.Put, $"{Rota}/{a}", token, Corpo(c.LocutorId, inicio, inicio.AddHours(1)), ETagVazio);
        Assert.Equal(HttpStatusCode.Conflict, cancelado.StatusCode);
        Assert.Equal("/erros/transicao-invalida", (await Json(cancelado)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Editar_programa_inexistente_e_404()
    {
        var c = NovaCena();
        var i = Agora().AddHours(2);

        var r = await Enviar(c.C, HttpMethod.Put, $"{Rota}/{Guid.NewGuid()}", await TokenDe(c.C, "admin@example.com"), Corpo(c.LocutorId, i, i.AddHours(1)), ETagVazio);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    // ---- cancelar e encerrar ----

    [Fact]
    public async Task Cancelar_grava_um_evento_no_outbox_e_cancelar_de_novo_e_409_sem_outro_evento()
    {
        var c = NovaCena();
        var token = await TokenDe(c.C, "admin@example.com");
        var inicio = Agora().AddHours(2);
        var id = await CriarComoAdmin(c, inicio, inicio.AddHours(1));

        var r = await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/cancelar", token);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("Cancelado", (await Json(r)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Enviar(c.C, HttpMethod.Post, $"{Rota}/{id}/cancelar", token)).StatusCode);
        Assert.Equal(1, EventosDe(c.F));
    }

    [Fact]
    public async Task Encerrar_com_a_janela_em_curso_grava_um_evento_e_antes_do_inicio_e_409()
    {
        var c = NovaCena();
        var token = await TokenDe(c.C, "admin@example.com");
        var emCurso = await CriarComoAdmin(c, Agora().AddSeconds(-10), Agora().AddHours(1));
        var futuro = await CriarComoAdmin(c, Agora().AddHours(5), Agora().AddHours(6));

        var ok = await Enviar(c.C, HttpMethod.Post, $"{Rota}/{emCurso}/encerrar", token);
        var cedo = await Enviar(c.C, HttpMethod.Post, $"{Rota}/{futuro}/encerrar", token);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Finalizado", (await Json(ok)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, cedo.StatusCode);
        Assert.Equal(1, EventosDe(c.F));
    }

    [Fact]
    public async Task Encerrar_programa_inexistente_e_404()
    {
        var c = NovaCena();

        Assert.Equal(HttpStatusCode.NotFound, (await Enviar(c.C, HttpMethod.Post, $"{Rota}/{Guid.NewGuid()}/encerrar", await TokenDe(c.C, "admin@example.com"))).StatusCode);
    }

    private static int EventosDe(ApiFactory f)
    {
        using var escopo = f.Services.CreateScope();
        return escopo.ServiceProvider.GetRequiredService<RadioDbContext>().EventosOutbox.Count(e => e.Tipo == "ProgramaStatusAlterado");
    }

    // ---- consultas ----

    [Fact]
    public async Task Lista_exige_os_dois_parametros_em_UTC_e_no_maximo_31_dias()
    {
        var c = NovaCena();
        var de = Agora();

        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Get, Rota)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Get, $"{Rota}?deUtc={Iso(de)}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Get, $"{Rota}?deUtc=ontem&ateUtc=hoje")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Get, $"{Rota}?deUtc=2030-10-20T10:00:00-03:00&ateUtc=2030-10-21T10:00:00-03:00")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enviar(c.C, HttpMethod.Get, $"{Rota}?deUtc={Iso(de)}&ateUtc={Iso(de.AddDays(32))}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Enviar(c.C, HttpMethod.Get, $"{Rota}?deUtc={Iso(de)}&ateUtc={Iso(de.AddDays(31))}")).StatusCode);
    }

    [Fact]
    public async Task Cancelados_so_aparecem_para_o_Admin_que_pede_e_o_anonimo_que_pede_e_ignorado()
    {
        var c = NovaCena();
        var token = await TokenDe(c.C, "admin@example.com");
        var inicio = Agora().AddHours(2);
        await CriarComoAdmin(c, inicio, inicio.AddHours(1), titulo: "Fica");
        var sai = await CriarComoAdmin(c, inicio.AddHours(2), inicio.AddHours(3), titulo: "Sai");
        await Enviar(c.C, HttpMethod.Post, $"{Rota}/{sai}/cancelar", token);
        var url = $"{Rota}?deUtc={Iso(inicio.AddHours(-1))}&ateUtc={Iso(inicio.AddHours(5))}";

        async Task<string[]> Titulos(string u, string? t) => (await Json(await Enviar(c.C, HttpMethod.Get, u, t))).EnumerateArray().Select(p => p.GetProperty("titulo").GetString()!).ToArray();

        Assert.Equal(["Fica"], await Titulos(url, null));
        Assert.Equal(["Fica"], await Titulos(url + "&incluirCancelados=true", null)); // anônimo: ignorado
        Assert.Equal(["Fica"], await Titulos(url + "&incluirCancelados=true", await TokenDe(c.C, "locutor@example.com")));
        Assert.Equal(["Fica", "Sai"], await Titulos(url + "&incluirCancelados=true", token));
    }

    [Fact]
    public async Task Ao_vivo_devolve_200_ou_204_e_por_id_traz_ETag_e_404()
    {
        var c = NovaCena();
        var inicio = Agora().AddHours(2);
        var id = await CriarComoAdmin(c, inicio, inicio.AddHours(1));

        var porId = await Enviar(c.C, HttpMethod.Get, $"{Rota}/{id}");

        Assert.Equal(HttpStatusCode.OK, porId.StatusCode);
        Assert.NotNull(porId.Headers.ETag);
        Assert.Equal(HttpStatusCode.NotFound, (await Enviar(c.C, HttpMethod.Get, $"{Rota}/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Enviar(c.C, HttpMethod.Get, $"{Rota}/ao-vivo")).StatusCode);
    }
}
