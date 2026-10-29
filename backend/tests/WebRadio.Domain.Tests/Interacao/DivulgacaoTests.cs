using WebRadio.Domain.Interacao;
using Xunit;

namespace WebRadio.Domain.Tests.Interacao;

public class DivulgacaoTests
{
    private static readonly DateTime Agora = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private static Divulgacao Criar(DateTime? inicio = null, DateTime? fim = null, byte prioridade = 50) =>
        new(Guid.NewGuid(), "Título", "Mensagem", null, "https://example.com", prioridade, inicio, fim, Guid.NewGuid(), Agora);

    [Fact]
    public void Construtor_rejeita_link_sem_https()
    {
        Assert.Throws<ArgumentException>(() =>
            new Divulgacao(Guid.NewGuid(), "T", "M", null, "http://inseguro.com", 0, null, null, Guid.NewGuid(), Agora));
    }

    [Theory]
    [InlineData("https://example.com/promo?a=1#x", "https://example.com/promo?a=1#x")]
    [InlineData("HTTPS://Example.COM", "https://example.com/")]
    [InlineData("https://sub.exemplo.com.br:8443/p", "https://sub.exemplo.com.br:8443/p")]
    [InlineData("https://exemplo.com.", "https://exemplo.com./")]
    public void Link_valido_e_gravado_na_forma_canonica(string entrada, string esperado)
    {
        var d = new Divulgacao(Guid.NewGuid(), "T", "M", null, entrada, 0, null, null, Guid.NewGuid(), Agora);

        Assert.Equal(esperado, d.LinkDestino);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//example.com")]
    [InlineData("https://user@example.com")]
    [InlineData("https://user:pass@example.com")]
    [InlineData("https://localhost")]
    [InlineData("https://LOCALHOST/x")]
    [InlineData("https://intranet")]
    [InlineData("https://app.local")]
    [InlineData("https://api.internal/x")]
    [InlineData("https://foo.localhost")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://10.0.0.5/x")]
    [InlineData("https://2130706433/")]
    [InlineData("https://0x7f.0.0.1/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://[::ffff:7f00:1]/")]
    [InlineData("https://exemplo.com/a b")]
    [InlineData("https://exemplo.com/\n")]
    [InlineData("https://exemplo.com/\t")]
    [InlineData(" https://exemplo.com")]
    [InlineData("")]
    public void Link_fora_da_regra_S_B03_e_rejeitado(string entrada)
    {
        Assert.Throws<ArgumentException>(() => new Divulgacao(Guid.NewGuid(), "T", "M", null, entrada, 0, null, null, Guid.NewGuid(), Agora));
    }

    [Fact]
    public void Link_acima_de_2048_caracteres_e_rejeitado()
    {
        var longo = "https://exemplo.com/" + new string('a', 2048);

        Assert.Throws<ArgumentException>(() => new Divulgacao(Guid.NewGuid(), "T", "M", null, longo, 0, null, null, Guid.NewGuid(), Agora));
    }

    [Fact]
    public void Editar_aplica_a_mesma_regra_do_link_e_nao_muda_nada_se_ela_falhar()
    {
        var d = Criar();

        Assert.Throws<ArgumentException>(() => d.Editar("Novo", "M", null, "https://127.0.0.1", 10, null, null, Agora));

        Assert.Equal("Título", d.Titulo);
        d.Editar("Novo", "M", null, "https://novo.exemplo.com", 10, null, null, Agora);
        Assert.Equal("https://novo.exemplo.com/", d.LinkDestino);
    }

    [Fact]
    public void Construtor_rejeita_prioridade_acima_de_100()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Criar(prioridade: 101));
    }

    [Fact]
    public void Construtor_rejeita_janela_invertida()
    {
        Assert.Throws<ArgumentException>(() => Criar(inicio: Agora.AddDays(1), fim: Agora));
    }

    [Fact]
    public void EstaAtivaParaPublico_sem_janela_e_ativo_retorna_true()
    {
        var divulgacao = Criar();
        Assert.True(divulgacao.EstaAtivaParaPublico(Agora));
    }

    [Fact]
    public void EstaAtivaParaPublico_fora_da_janela_retorna_false()
    {
        var divulgacao = Criar(inicio: Agora.AddDays(1), fim: Agora.AddDays(2));
        Assert.False(divulgacao.EstaAtivaParaPublico(Agora));
    }

    [Fact]
    public void EstaAtivaParaPublico_desativada_retorna_false_mesmo_dentro_da_janela()
    {
        var divulgacao = Criar(inicio: Agora.AddDays(-1), fim: Agora.AddDays(1));
        divulgacao.Desativar(Agora);

        Assert.False(divulgacao.EstaAtivaParaPublico(Agora));
    }

    [Fact]
    public void Fim_da_janela_e_exclusivo()
    {
        var divulgacao = Criar(inicio: Agora.AddDays(-1), fim: Agora);
        Assert.False(divulgacao.EstaAtivaParaPublico(Agora));
    }

    [Fact]
    public void Ativar_depois_de_Desativar_volta_a_aparecer()
    {
        var divulgacao = Criar();
        divulgacao.Desativar(Agora.AddMinutes(1));
        divulgacao.Ativar(Agora.AddMinutes(2));

        Assert.True(divulgacao.EstaAtivaParaPublico(Agora.AddMinutes(3)));
    }

    [Fact]
    public void Editar_aplica_novos_valores()
    {
        var divulgacao = Criar();

        divulgacao.Editar("Novo título", "Nova mensagem", null, "https://novo.com", 10, null, null, Agora.AddMinutes(1));

        Assert.Equal("Novo título", divulgacao.Titulo);
        Assert.Equal(10, divulgacao.Prioridade);
    }
}
