using System.Security.Cryptography;
using System.Text;
using WebRadio.Application.Features.Transmissao;
using Xunit;

namespace WebRadio.Application.Tests.Transmissao;

public class SegredoDeTransmissaoTests
{
    [Fact]
    public void Gerar_produz_segredo_de_43_caracteres_base64url()
    {
        var g = SegredoDeTransmissao.Gerar();

        Assert.Equal(43, g.Segredo.Length);
        Assert.True(SegredoDeTransmissao.SenhaValida(g.Segredo));
        Assert.Equal(16, g.Salt.Length);
        Assert.Equal(32, g.Hash.Length);
    }

    [Fact]
    public void Gerar_nunca_repete_segredo_nem_salt()
    {
        var a = SegredoDeTransmissao.Gerar();
        var b = SegredoDeTransmissao.Gerar();

        Assert.NotEqual(a.Segredo, b.Segredo);
        Assert.NotEqual(a.Salt, b.Salt);
    }

    [Fact]
    public void Hash_e_sha256_do_salt_seguido_do_texto_ascii()
    {
        var g = SegredoDeTransmissao.Gerar();

        var esperado = SHA256.HashData([.. g.Salt, .. Encoding.ASCII.GetBytes(g.Segredo)]);

        Assert.Equal(esperado, g.Hash);
        Assert.Equal(esperado, SegredoDeTransmissao.Hash(g.Salt, g.Segredo));
    }

    [Fact]
    public void Confere_aceita_o_segredo_certo_e_recusa_outro()
    {
        var g = SegredoDeTransmissao.Gerar();
        var outro = SegredoDeTransmissao.Gerar();

        Assert.True(SegredoDeTransmissao.Confere(g.Salt, g.Hash, g.Segredo));
        Assert.False(SegredoDeTransmissao.Confere(g.Salt, g.Hash, outro.Segredo));
    }

    [Fact]
    public void Identificador_novo_e_uuid_v4_no_formato_do_contrato()
    {
        var id = SegredoDeTransmissao.NovoIdentificador();

        Assert.Equal(4, id.Version);
        Assert.True(SegredoDeTransmissao.UsuarioValido(id.ToString("D")));
    }

    [Theory]
    [InlineData("3f2b1c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", true)]
    [InlineData("3F2B1C4D-5E6F-4A7B-8C9D-0E1F2A3B4C5D", false)] // o contrato é minúsculo
    [InlineData("3f2b1c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d\n", false)]
    [InlineData("{3f2b1c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d}", false)]
    [InlineData("3f2b1c4d5e6f4a7b8c9d0e1f2a3b4c5d", false)]
    [InlineData("../3f2b1c4d-5e6f-4a7b-8c9d-0e1f2a3b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void UsuarioValido_so_aceita_guid_minusculo_com_hifens(string? usuario, bool valido)
    {
        Assert.Equal(valido, SegredoDeTransmissao.UsuarioValido(usuario));
    }

    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNO_-", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMN_-", false)]   // 42
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOP_-", false)] // 44
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMN_-\n", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNO+/", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOá-", false)]
    [InlineData(null, false)]
    public void SenhaValida_so_aceita_43_caracteres_do_alfabeto_base64url(string? senha, bool valida)
    {
        Assert.Equal(valida, SegredoDeTransmissao.SenhaValida(senha));
    }

    [Fact]
    public void ConferirFicticio_nao_lanca()
    {
        SegredoDeTransmissao.ConferirFicticio(SegredoDeTransmissao.Gerar().Segredo);
    }
}
