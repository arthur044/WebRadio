using System.Net;
using WebRadio.Application.Features.Auth;
using WebRadio.Infrastructure.Seguranca;
using Xunit;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Auth;

public class NormalizacaoTests
{
    [Theory]
    [InlineData("jose@x.com")]
    [InlineData("JOSE@X.COM")]
    [InlineData("José@x.com")]
    [InlineData("  josé@X.com ")]
    [InlineData("ｊｏｓｅ@x.com")]           // largura total (NFKC)
    [InlineData("jose\u0301@x.com")]        // e + acento combinante
    public void Variantes_visualmente_iguais_dao_a_mesma_chave_do_throttle_mais_grossa_que_a_conta(string email)
        => Assert.Equal("JOSE@X.COM", EmailChave.Normalizar(email));

    [Fact]
    public async Task Handler_usa_a_chave_grossa_no_throttle_para_todas_as_variantes()
    {
        var throttle = new ThrottleFalso();
        var handler = new LoginHandler(NovoDb(), new HasherContador(), Emissor(), throttle, new RelogioFalso(Agora));
        foreach (var v in new[] { "jose@x.com", "José@x.com", "ｊｏｓｅ@x.com" })
            await Assert.ThrowsAsync<WebRadio.Domain.Erros.CredenciaisInvalidasException>(() => handler.Handle(new LoginCommand(v, SenhaOk, IpHash), default));

        Assert.Single(throttle.Chaves.Distinct());
    }

    [Fact]
    public void IpHash_do_IPv6_agrupa_por_64_e_o_IPv4_mapeado_vale_o_IPv4()
    {
        var h = new IpHasher(new byte[32]);
        Assert.Equal(h.Hash(IPAddress.Parse("2001:db8:1:2::1")), h.Hash(IPAddress.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff")));
        Assert.NotEqual(h.Hash(IPAddress.Parse("2001:db8:1:2::1")), h.Hash(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal(h.Hash(IPAddress.Parse("203.0.113.9")), h.Hash(IPAddress.Parse("::ffff:203.0.113.9")));
        Assert.NotEqual(h.Hash(IPAddress.Parse("203.0.113.9")), h.Hash(IPAddress.Parse("203.0.113.10")));
    }

    [Fact]
    public void Pseudonimo_e_curto_estavel_e_depende_do_pepper()
    {
        var a = new IpHasher(new byte[32]);
        var b = new IpHasher(Enumerable.Repeat((byte)1, 32).ToArray());
        Assert.Equal(8, a.Pseudonimo("X@Y.COM").Length);
        Assert.Equal(a.Pseudonimo("X@Y.COM"), a.Pseudonimo("X@Y.COM"));
        Assert.NotEqual(a.Pseudonimo("X@Y.COM"), b.Pseudonimo("X@Y.COM"));
    }
}
