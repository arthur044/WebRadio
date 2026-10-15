using Microsoft.Extensions.Configuration;
using WebRadio.Api.Infra;
using Xunit;

namespace WebRadio.Api.IntegrationTests;

/// <summary>#19: a allowlist de origens é normalizada UMA vez (no boot), não ligada à configuração a cada requisição.</summary>
public class OrigensPermitidasTests
{
    private static OrigensPermitidas Com(params string[] origens) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(origens.Select((o, i) => KeyValuePair.Create<string, string?>($"Seguranca:OrigensPermitidas:{i}", o))).Build());

    [Theory]
    [InlineData("https://app.example.com")]
    [InlineData("https://APP.example.com")]
    [InlineData("https://app.example.com/")]
    public void Normaliza_caixa_e_barra_final(string recebida)
        => Assert.True(Com("https://App.Example.com/").Contem(recebida));

    [Fact]
    public void Porta_faz_parte_da_origem_e_porta_padrao_e_omitida()
    {
        var o = Com("https://x.test:8443", "https://y.test:443");

        Assert.True(o.Contem("https://x.test:8443"));
        Assert.False(o.Contem("https://x.test"));
        Assert.True(o.Contem("https://y.test"));
    }

    [Theory]
    [InlineData("ftp://app.example.com")]
    [InlineData("app.example.com")]
    [InlineData("https://app.example.com/caminho")] // origem não tem caminho
    [InlineData("")]
    public void Entradas_que_nao_sao_origem_http_s_sao_ignoradas(string invalida)
        => Assert.False(Com(invalida).Contem(invalida));

    [Fact]
    public void Sem_configuracao_nenhuma_origem_extra_vale()
        => Assert.False(Com().Contem("https://app.example.com"));

    [Fact]
    public void Origem_vazia_ou_nula_nunca_vale()
    {
        var o = Com("https://app.example.com");

        Assert.False(o.Contem(null));
        Assert.False(o.Contem(""));
    }
}
