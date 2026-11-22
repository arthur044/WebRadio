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

    private static Microsoft.AspNetCore.Http.DefaultHttpContext Req(string esquema, string host, string origem, string? fetchSite = null)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Scheme = esquema;
        http.Request.Host = new Microsoft.AspNetCore.Http.HostString(host);
        http.Request.Headers.Origin = origem;
        if (fetchSite is not null) http.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        return http;
    }

    // Admin pôs PUBLIC_AUTHORITY=radio.x:443: o Host chega com :443 e o Origin do navegador, sem. Antes: 403 em todo refresh/logout.
    [Theory]
    [InlineData("https", "radio.x:443", "https://radio.x")]
    [InlineData("https", "radio.x", "https://radio.x:443")]
    [InlineData("http", "radio.x:80", "http://radio.x")]
    [InlineData("https", "Radio.X", "https://radio.x")]
    [InlineData("https", "localhost:8080", "https://localhost:8080")]
    public void Propria_origem_casa_com_ou_sem_a_porta_padrao(string esquema, string host, string origem)
        => Assert.True(OrigemDaRequisicao.EhPermitida(Req(esquema, host, origem, "same-origin"), Com()));

    [Theory]
    [InlineData("https", "radio.x:8443", "https://radio.x")]   // porta não padrão faz diferença
    [InlineData("https", "radio.x", "http://radio.x")]          // esquema diferente
    [InlineData("https", "radio.x", "https://evil.example")]
    [InlineData("https", "radio.x", "null")]
    [InlineData("https", "radio.x", "")]                         // Origin ausente
    public void Origem_que_nao_e_a_propria_e_recusada(string esquema, string host, string origem)
        => Assert.False(OrigemDaRequisicao.EhPermitida(Req(esquema, host, origem), Com()));

    [Fact]
    public void Propria_origem_com_Sec_Fetch_Site_cross_site_continua_recusada()
        => Assert.False(OrigemDaRequisicao.EhPermitida(Req("https", "radio.x:443", "https://radio.x", "cross-site"), Com()));
}
