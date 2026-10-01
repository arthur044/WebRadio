using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WebRadio.Domain.Enums;
using WebRadio.Infrastructure.Seguranca;
using Xunit;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Auth;

public class EmissorDeTokensTests
{
    [Fact]
    public void Access_token_tem_claims_issuer_audience_kid_e_15_minutos()
    {
        var chave = System.Security.Cryptography.RandomNumberGenerator.GetBytes(48);
        var u = CriarUsuario(NovoDb(), new HasherContador(), role: Role.Admin, deveTrocar: true);
        var t = new EmissorDeTokens(chave, new RelogioFalso(Agora)).EmitirAccessToken(u);
        var jwt = new JsonWebToken(t.Token);

        Assert.Equal(u.Id.ToString(), jwt.GetClaim("sub").Value);
        Assert.Equal("Admin", jwt.GetClaim("role").Value);
        Assert.Equal("Ana", jwt.GetClaim("name").Value);
        Assert.Equal("true", jwt.GetClaim(EmissorDeTokens.ClaimDeveTrocarSenha).Value);
        Assert.NotEmpty(jwt.Id);
        Assert.Equal("webradio-api", jwt.Issuer);
        Assert.Contains("webradio", jwt.Audiences);
        Assert.Equal("HS256", jwt.Alg);
        Assert.Equal(EmissorDeTokens.KidDe(chave), jwt.Kid);
        Assert.DoesNotContain(Convert.ToHexString(chave), jwt.Kid, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Agora.AddMinutes(15), t.ExpiraEmUtc);
    }

    [Fact]
    public void Token_sem_DeveTrocarSenha_nao_carrega_o_claim()
    {
        var u = CriarUsuario(NovoDb(), new HasherContador());
        Assert.Null(new JsonWebToken(Emissor().EmitirAccessToken(u).Token).TryGetClaim(EmissorDeTokens.ClaimDeveTrocarSenha, out var c) ? c : null);
    }

    [Fact]
    public void Refresh_e_aleatorio_de_256_bits_e_o_hash_e_SHA256_do_valor()
    {
        var e = Emissor();
        var a = e.NovoRefresh();
        var b = e.NovoRefresh();
        Assert.NotEqual(a.Token, b.Token);
        Assert.Equal(32, a.Hash.Length);
        Assert.Equal(a.Hash, System.Security.Cryptography.SHA256.HashData(System.Buffers.Text.Base64Url.DecodeFromChars(a.Token)));
        Assert.Equal(TimeSpan.FromDays(7), e.DuracaoDoRefresh);
    }
}
