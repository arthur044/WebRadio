using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebRadio.Api.Infra;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Enums;
using Xunit;
using static WebRadio.Api.IntegrationTests.ApiFactoryExtensions;

namespace WebRadio.Api.IntegrationTests;

/// <summary>#9: o alerta de 401/s no login vira o log estruturado <c>AUTH_401_TAXA</c> por minuto (sem sistema de alerta ainda). Só login, só 401, sem PII.</summary>
public class Taxa401DoLoginTests
{
    private sealed class LogFalso : ILogger<Taxa401DoLoginJob>
    {
        public List<(LogLevel Nivel, string Texto, IReadOnlyList<KeyValuePair<string, object?>> Estado)> Linhas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Linhas.Add((logLevel, formatter(state, exception), (state as IReadOnlyList<KeyValuePair<string, object?>>) ?? []));
    }

    [Fact]
    public void Sem_401_no_minuto_nao_loga_e_com_401_loga_o_total_e_a_taxa_por_segundo_e_zera_o_contador()
    {
        var contador = new ContadorDe401DoLogin();
        var log = new LogFalso();
        var job = new Taxa401DoLoginJob(contador, log);

        job.Emitir();
        Assert.Empty(log.Linhas); // minuto calmo: sem ruído

        for (var i = 0; i < 120; i++) contador.Registrar();
        job.Emitir();

        var linha = Assert.Single(log.Linhas);
        Assert.Equal(LogLevel.Warning, linha.Nivel);
        Assert.StartsWith("AUTH_401_TAXA", linha.Texto);
        Assert.Contains(linha.Estado, p => p.Key == "Total" && Equals(p.Value, 120L));
        Assert.Contains(linha.Estado, p => p.Key == "JanelaSegundos" && Equals(p.Value, 60));
        Assert.Contains(linha.Estado, p => p.Key == "PorSegundo" && p.Value is double d && Math.Abs(d - 2.0) < 1e-9);

        job.Emitir();
        Assert.Single(log.Linhas); // o contador zerou: o minuto seguinte, sem 401, não loga
    }

    [Fact]
    public async Task Login_com_senha_errada_conta_401_mas_login_certo_e_outras_rotas_nao()
    {
        var f = new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.40") };
        f.CriarUsuario("ana@example.com", Role.Locutor);
        var c = f.CreateClient(new() { BaseAddress = new Uri("http://localhost") });
        var contador = f.Services.GetRequiredService<ContadorDe401DoLogin>();
        contador.Coletar(); // zera qualquer resíduo

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/auth/login", new { email = "ana@example.com", senha = "senha-errada-123" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/auth/login", new { email = "ninguem@example.com", senha = "senha-errada-123" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/auth/login", new { email = "ana@example.com", senha = Senha })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/auth/me")).StatusCode); // 401 de outra rota: não é de login

        Assert.Equal(2, contador.Coletar());
    }

    [Theory]
    [InlineData(3, LogLevel.Information)]
    [InlineData(59, LogLevel.Information)]
    [InlineData(60, LogLevel.Warning)] // 1 por segundo na média
    [InlineData(600, LogLevel.Warning)]
    public void Abaixo_de_1_por_segundo_e_Information_e_a_partir_de_1_por_segundo_e_Warning(int total, LogLevel esperado)
    {
        var contador = new ContadorDe401DoLogin();
        var log = new LogFalso();
        for (var i = 0; i < total; i++) contador.Registrar();

        new Taxa401DoLoginJob(contador, log).Emitir();

        Assert.Equal(esperado, Assert.Single(log.Linhas).Nivel);
    }

    [Theory]
    [InlineData("/api/v1/auth/login/")]
    [InlineData("/API/V1/AUTH/LOGIN")]
    [InlineData("/api/v1/Auth/Login/")]
    public async Task Variacoes_do_path_do_login_casam_o_mesmo_endpoint_e_tambem_contam(string caminho)
    {
        var f = new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.42") };
        f.CriarUsuario("ana@example.com", Role.Locutor);
        var c = f.CreateClient(new() { BaseAddress = new Uri("http://localhost") });
        var contador = f.Services.GetRequiredService<ContadorDe401DoLogin>();
        contador.Coletar();

        var r = await c.PostAsJsonAsync(caminho, new { email = "ana@example.com", senha = "senha-errada-123" });

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode); // a barra final e a caixa não escapam do endpoint
        Assert.Equal(1, contador.Coletar()); // nem do alerta de força bruta
    }

    /// <summary>O semáforo global de PBKDF2 (R1) com todas as vagas ocupadas: a espera estoura e o hasher lança <see cref="ServidorOcupadoException"/>.</summary>
    private sealed class HasherLotado : ISenhaHasher
    {
        public string Hash(string senha) => throw new ServidorOcupadoException();

        public ResultadoSenha Verificar(string hash, string senha) => throw new ServidorOcupadoException();

        public void VerificarFicticio(string senha) => throw new ServidorOcupadoException();
    }

    [Theory]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/v1/auth/registrar")]
    public async Task Com_o_semaforo_de_PBKDF2_lotado_login_e_registrar_devolvem_503_com_Retry_After_e_nao_401(string rota)
    {
        var f = new ApiFactory { RemoteIp = IPAddress.Parse("203.0.113.41") };
        f.CriarUsuario("ana@example.com", Role.Locutor);
        var c = f.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ISenhaHasher>(new HasherLotado())))
            .CreateClient(new() { BaseAddress = new Uri("http://localhost") });
        var contador = f.Services.GetRequiredService<ContadorDe401DoLogin>();
        contador.Coletar();

        var r = await c.PostAsJsonAsync(rota, rota.EndsWith("login")
            ? new { email = "ana@example.com", senha = Senha }
            : (object)new { nome = "Bia", email = "bia@example.com", senha = "uma-senha-bem-longa-456" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.True(r.Headers.RetryAfter is not null);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, contador.Coletar()); // 503 não é 401: lotação não vira falso alarme de força bruta
    }
}
