using Microsoft.AspNetCore.Http;
using System.Net;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using WebRadio.Api.Configuracao;
using WebRadio.Api.Erros;
using WebRadio.Application.Comum;
using WebRadio.Domain.Erros;
using Xunit;

namespace WebRadio.Api.IntegrationTests;

/// <summary>S-A11: ForwardedHeaders com KnownNetworks e ForwardLimit = 1, usando a mesma extensão que o Program aplica.</summary>
public class ForwardedHeadersTests
{
    private static async Task<WebApplication> SubirAsync(string? knownNetworks = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (knownNetworks is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Rede:KnownNetworks:0"] = knownNetworks });
        }

        builder.Services.AddEncaminhamentoSeguro(builder.Configuration);

        var app = builder.Build();
        app.Use(async (contexto, proximo) =>
        {
            // O TestServer não tem socket: o IP "do socket" vem do teste.
            contexto.Connection.RemoteIpAddress = IPAddress.Parse(contexto.Request.Headers["X-Socket-Ip"].ToString());
            await proximo();
        });
        app.UseForwardedHeaders();
        app.MapGet("/ip", (HttpContext c) => $"{c.Connection.RemoteIpAddress}|{c.Request.Scheme}");
        await app.StartAsync();
        return app;
    }

    private static async Task<string> Consultar(WebApplication app, string ipDoSocket, string? xff, string? proto = null)
    {
        var cliente = app.GetTestClient();
        var requisicao = new HttpRequestMessage(HttpMethod.Get, "/ip");
        requisicao.Headers.Add("X-Socket-Ip", ipDoSocket);
        if (xff is not null)
        {
            requisicao.Headers.Add("X-Forwarded-For", xff);
        }

        if (proto is not null)
        {
            requisicao.Headers.Add("X-Forwarded-Proto", proto);
        }

        return await (await cliente.SendAsync(requisicao)).Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task XFF_forjado_de_fora_da_KnownNetworks_e_ignorado()
    {
        await using var app = await SubirAsync();

        Assert.Equal("203.0.113.9|http", await Consultar(app, "203.0.113.9", "198.51.100.1", "https"));
    }

    [Fact]
    public async Task XFF_vindo_do_proxy_na_KnownNetworks_vale()
    {
        await using var app = await SubirAsync();

        Assert.Equal("198.51.100.7|https", await Consultar(app, "172.20.0.5", "198.51.100.7", "https"));
    }

    [Fact]
    public async Task ForwardLimit_1_usa_so_o_ultimo_salto_e_ignora_o_forjado_antes_dele()
    {
        await using var app = await SubirAsync();

        // O cliente mandou "1.1.1.1"; o proxy anexou o IP real. Só o último salto é confiável.
        Assert.Equal("198.51.100.7|http", await Consultar(app, "172.20.0.5", "1.1.1.1, 198.51.100.7"));
    }

    [Fact]
    public async Task A_subnet_vem_da_configuracao()
    {
        await using var app = await SubirAsync(knownNetworks: "10.9.0.0/16");

        Assert.Equal("198.51.100.7|http", await Consultar(app, "10.9.3.4", "198.51.100.7"));
        Assert.Equal("172.20.0.5|http", await Consultar(app, "172.20.0.5", "198.51.100.7")); // subnet padrão não vale mais
    }
}

public class MascaraDeSegredosTests
{
    private static (LogEvent Evento, List<LogEvent> Todos) Registrar(Action<ILogger> acao)
    {
        var eventos = new List<LogEvent>();
        using var logger = new LoggerConfiguration()
            .Enrich.With<MascaraSegredosEnricher>()
            .WriteTo.Sink(new ColetorDeEventos(eventos))
            .CreateLogger();

        acao(logger);
        return (eventos[0], eventos);
    }

    private sealed class ColetorDeEventos(List<LogEvent> destino) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent) => destino.Add(logEvent);
    }

    private sealed record Credencial(string Email, string Senha, string RefreshToken, string Nome);

    [Fact]
    public void Mascara_propriedades_escalares_sensiveis_e_preserva_as_demais()
    {
        var (evento, _) = Registrar(l => l.Information(
            "{Password} {SenhaAtual} {AccessToken} {SigningKey} {Pepper} {Email} {Quantidade} {Rota}",
            "p1", "p2", "t", "k", "pp", "ana@example.com", 3, "/api/v1/auth/login"));

        foreach (var nome in new[] { "Password", "SenhaAtual", "AccessToken", "SigningKey", "Pepper", "Email" })
        {
            Assert.Equal("\"***\"", evento.Properties[nome].ToString());
        }

        Assert.Equal("3", evento.Properties["Quantidade"].ToString());
        Assert.Equal("\"/api/v1/auth/login\"", evento.Properties["Rota"].ToString());
    }

    [Fact]
    public void Mascara_dentro_de_objetos_destruturados_e_colecoes()
    {
        var (evento, _) = Registrar(l => l.Information(
            "{@Credencial} {@Itens}",
            new Credencial("ana@example.com", "segredo", "abc", "Ana"),
            new Dictionary<string, string> { ["Authorization"] = "Bearer xyz", ["Rota"] = "/x" }));

        var texto = string.Join(" ", evento.Properties.Select(p => p.Value.ToString()));

        Assert.DoesNotContain("ana@example.com", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("segredo", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer xyz", texto, StringComparison.Ordinal);
        Assert.Contains("Ana", texto, StringComparison.Ordinal);
        Assert.Contains("/x", texto, StringComparison.Ordinal);
    }
}

public class MapeamentoDeErrosTests
{
    [Fact]
    public void Tabela_de_01_dominio_secao_3()
    {
        void Confere(Exception ex, int status, string tipo)
        {
            var problema = ProblemasHttp.Mapear(ex);
            Assert.NotNull(problema);
            Assert.Equal(status, problema.Status);
            Assert.Equal(tipo, problema.Type);
        }

        Confere(new CredenciaisInvalidasException(), 401, "/erros/credenciais");
        Confere(new NaoEncontradoException("Programa"), 404, "/erros/nao-encontrado");
        Confere(new ConflitoDeGradeException(Guid.NewGuid()), 409, "/erros/conflito-grade");
        Confere(new TransicaoInvalidaException("x"), 409, "/erros/transicao-invalida");
        Confere(new PedidoJaModeradoException(Guid.NewGuid()), 409, "/erros/transicao-invalida");
        Confere(new VersaoDesatualizadaException(), 412, "/erros/versao-desatualizada");
        Confere(new MuitosPedidosException(TimeSpan.FromSeconds(90)), 429, "/erros/muitos-pedidos");
        Confere(new EmailJaCadastradoException(), 409, "/erros/email-ja-cadastrado");
    }

    [Fact]
    public void Conflito_de_grade_e_429_carregam_as_extensoes()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, ProblemasHttp.Mapear(new ConflitoDeGradeException(id))!.Extensions["programaConflitanteId"]);
        Assert.Equal(90, ProblemasHttp.Mapear(new MuitosPedidosException(TimeSpan.FromSeconds(90)))!.Extensions["retryAfterSeconds"]);
    }

    [Fact]
    public void Validacao_agrupa_por_campo_em_camelCase()
    {
        var problema = ProblemasHttp.Mapear(new ValidationException(
        [
            new ValidationFailure("NovaSenha", "curta"),
            new ValidationFailure("NovaSenha", "comum"),
            new ValidationFailure("SenhaAtual", "obrigatória"),
        ]))!;

        var erros = (Dictionary<string, string[]>)problema.Extensions["errors"]!;
        Assert.Equal(["curta", "comum"], erros["novaSenha"]);
        Assert.Equal(["obrigatória"], erros["senhaAtual"]);
    }

    [Fact]
    public void Excecao_desconhecida_nao_e_mapeada_e_vira_500_sem_vazar_detalhe()
    {
        Assert.Null(ProblemasHttp.Mapear(new InvalidOperationException("Server=prod;Password=segredo")));
    }
}
