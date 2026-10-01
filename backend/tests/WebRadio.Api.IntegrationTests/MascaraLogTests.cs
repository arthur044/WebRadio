using Serilog;
using Serilog.Core;
using Serilog.Events;
using WebRadio.Api.Infra;
using Xunit;

namespace WebRadio.Api.IntegrationTests;

public class MascaraLogTests
{
    private sealed class Captura : ILogEventSink
    {
        public List<LogEvent> Eventos { get; } = [];
        public void Emit(LogEvent e) => Eventos.Add(e);
    }

    private sealed record Login(string Email, string SenhaAtual, string AccessToken, string Jwt__SigningKey, string Pepper, string DbPassword, string Nome);

    [Fact]
    public void Propriedades_sensiveis_sao_mascaradas_ao_destruturar()
    {
        var sink = new Captura();
        using var log = new LoggerConfiguration().Destructure.With<MascaraSegredosPolicy>().WriteTo.Sink(sink).CreateLogger();

        log.Information("{@Dados}", new Login("a@b.c", "s3nh4", "tok", "key", "pep", "pwd", "Ana"));

        var texto = sink.Eventos.Single().Properties["Dados"].ToString();
        foreach (var segredo in new[] { "s3nh4", "\"tok\"", "\"key\"", "\"pep\"", "\"pwd\"" })
            Assert.DoesNotContain(segredo, texto);
        Assert.DoesNotContain("a@b.c", texto);
        Assert.Contains("Ana", texto);
    }

    [Fact]
    public void Dicionarios_e_tipos_anonimos_tambem_sao_mascarados()
    {
        var sink = new Captura();
        using var log = new LoggerConfiguration().Destructure.With<MascaraSegredosPolicy>().WriteTo.Sink(sink).CreateLogger();

        log.Information("{@D} {@A}",
            new Dictionary<string, string> { ["Authorization"] = "Bearer abc", ["ok"] = "visivel" },
            new { RefreshToken = "r-xyz", ClientSecret = "cs", Linha = "visivel2" });

        var texto = string.Join(" ", sink.Eventos.Single().Properties.Values.Select(v => v.ToString()));
        foreach (var segredo in new[] { "Bearer abc", "r-xyz", "\"cs\"" }) Assert.DoesNotContain(segredo, texto);
        Assert.Contains("visivel", texto);
        Assert.Contains("visivel2", texto);
    }
}
