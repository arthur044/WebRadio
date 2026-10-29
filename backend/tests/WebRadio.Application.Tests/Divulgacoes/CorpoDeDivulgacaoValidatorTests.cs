using WebRadio.Application.Features.Divulgacoes;
using Xunit;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Divulgacoes;

/// <summary>07 E3-D5, E3-D7 e S-B03: a entrada do corpo de criar e de editar.</summary>
public class CorpoDeDivulgacaoValidatorTests
{
    private readonly CriarDivulgacaoValidator _v = new();

    private static CriarDivulgacaoCommand Corpo(string titulo = "Promo", string mensagem = "Mensagem", string? imagem = null, string? link = null, byte prioridade = 50,
        DateTime? inicio = null, DateTime? fim = null) => new(Guid.NewGuid(), titulo, mensagem, imagem, link, prioridade, inicio, fim, true);

    [Fact]
    public void Corpo_minimo_valido()
    {
        Assert.True(_v.Validate(Corpo()).IsValid);
        Assert.True(_v.Validate(Corpo(imagem: "divulgacoes/0123456789abcdef0123456789abcdef.webp", link: "https://exemplo.com")).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("titulo\ncom quebra")]
    [InlineData("titulo‮bidi")]
    public void Titulo_e_de_linha_unica_visivel_e_sem_bidi(string titulo) => Assert.False(_v.Validate(Corpo(titulo: titulo)).IsValid);

    [Fact]
    public void Titulo_vai_ate_120_e_mensagem_ate_1000_unidades_UTF16()
    {
        Assert.True(_v.Validate(Corpo(titulo: new string('a', 120))).IsValid);
        Assert.False(_v.Validate(Corpo(titulo: new string('a', 121))).IsValid);
        Assert.True(_v.Validate(Corpo(mensagem: new string('a', 1000))).IsValid);
        Assert.False(_v.Validate(Corpo(mensagem: new string('a', 1001))).IsValid);
        Assert.False(_v.Validate(Corpo(titulo: string.Concat(Enumerable.Repeat("\U0001F600", 61)))).IsValid);
    }

    [Fact]
    public void Mensagem_aceita_quebra_de_linha_mas_nao_bidi_nem_so_quebras()
    {
        Assert.True(_v.Validate(Corpo(mensagem: "linha 1\nlinha 2")).IsValid);
        Assert.False(_v.Validate(Corpo(mensagem: "\n\n")).IsValid);
        Assert.False(_v.Validate(Corpo(mensagem: "a‮b")).IsValid);
        Assert.False(_v.Validate(Corpo(mensagem: "a\r\nb")).IsValid);
    }

    [Theory]
    [InlineData("divulgacoes/0123456789abcdef0123456789abcdef.webp", true)]
    [InlineData("divulgacoes/0123456789ABCDEF0123456789abcdef.webp", false)]
    [InlineData("divulgacoes/0123456789abcdef0123456789abcdef.webp\n", false)]
    [InlineData("divulgacoes/0123456789abcdef0123456789abcde.webp", false)]
    [InlineData("divulgacoes/0123456789abcdef0123456789abcdef.jpg", false)]
    [InlineData("midia/0123456789abcdef0123456789abcdef.webp", false)]
    [InlineData("../divulgacoes/0123456789abcdef0123456789abcdef.webp", false)]
    [InlineData("divulgacoes/sub/0123456789abcdef0123456789abcdef.webp", false)]
    [InlineData("https://exemplo.com/a.webp", false)]
    public void Chave_da_imagem_so_no_formato_do_E3_D5(string chave, bool valida) => Assert.Equal(valida, _v.Validate(Corpo(imagem: chave)).IsValid);

    [Theory]
    [InlineData("http://exemplo.com")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://localhost")]
    [InlineData("https://user@exemplo.com")]
    [InlineData("https://exemplo.com/a b")]
    public void Link_fora_do_S_B03_e_400(string link) => Assert.False(_v.Validate(Corpo(link: link)).IsValid);

    [Fact]
    public void Prioridade_e_janela()
    {
        Assert.True(_v.Validate(Corpo(prioridade: 100)).IsValid);
        Assert.False(_v.Validate(Corpo(prioridade: 101)).IsValid);
        Assert.False(_v.Validate(Corpo(inicio: Agora, fim: Agora)).IsValid);
        Assert.False(_v.Validate(Corpo(inicio: Agora.AddHours(1), fim: Agora)).IsValid);
        Assert.True(_v.Validate(Corpo(inicio: Agora, fim: Agora.AddHours(1))).IsValid);
        Assert.False(_v.Validate(Corpo(inicio: DateTime.SpecifyKind(Agora, DateTimeKind.Unspecified))).IsValid);
        Assert.False(_v.Validate(Corpo(inicio: Agora.AddMilliseconds(5))).IsValid);
    }

    [Fact]
    public void A_chave_nova_gerada_pelo_servidor_passa_na_regra()
    {
        for (var i = 0; i < 50; i++)
            Assert.True(_v.Validate(Corpo(imagem: ChaveDeImagem.Nova())).IsValid);
    }
}
