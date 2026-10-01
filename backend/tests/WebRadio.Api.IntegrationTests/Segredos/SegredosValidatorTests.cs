using System.Buffers.Text;
using WebRadio.Api.Segredos;
using Xunit;

namespace WebRadio.Api.IntegrationTests.Segredos;

/// <summary>S-C02: fail-fast de segredos. Teste unitário (sem host), roda no job backend-unit.</summary>
public class SegredosValidatorTests
{
    // 48 bytes cujo base64url tem '-' e '_' e nenhum '=' (contrato do 05 §5.1).
    private static readonly string ChaveBase64UrlValida = Base64Url.EncodeToString(
        Enumerable.Range(0, 48).Select(i => (byte)(i % 2 == 0 ? 0xFB : 0xFF)).ToArray());

    [Fact]
    public void Chave_de_teste_usa_o_alfabeto_url_sem_padding()
    {
        Assert.Contains('-', ChaveBase64UrlValida);
        Assert.Contains('_', ChaveBase64UrlValida);
        Assert.DoesNotContain('=', ChaveBase64UrlValida);
    }

    [Fact]
    public void Jwt_chave_base64url_com_hifen_e_underscore_de_48_bytes_e_aceita()
        => Assert.True(new JwtOptionsValidator().Validate(null, new JwtOptions { SigningKey = ChaveBase64UrlValida }).Succeeded);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("__GERAR__")]
    [InlineData("change_me_change_me_change_me_change_me_x")]
    [InlineData("curta")]
    [InlineData("não é base64url!!!")]
    public void Jwt_chave_invalida_e_recusada(string valor)
        => Assert.True(new JwtOptionsValidator().Validate(null, new JwtOptions { SigningKey = valor }).Failed);

    [Fact]
    public void Jwt_chave_com_menos_de_32_bytes_decodificados_e_recusada()
    {
        var curta = Base64Url.EncodeToString(new byte[31]);
        Assert.True(new JwtOptionsValidator().Validate(null, new JwtOptions { SigningKey = curta }).Failed);
    }

    [Fact]
    public void Jwt_chave_com_marcador_dentro_do_valor_e_recusada()
        => Assert.True(new JwtOptionsValidator().Validate(null, new JwtOptions { SigningKey = ChaveBase64UrlValida + "__GERAR__" }).Failed);

    [Fact]
    public void Pepper_valido_e_recusado_quando_vazio_ou_curto()
    {
        var v = new SegurancaOptionsValidator();
        Assert.True(v.Validate(null, new SegurancaOptions { Pepper = ChaveBase64UrlValida }).Succeeded);
        Assert.True(v.Validate(null, new SegurancaOptions { Pepper = "" }).Failed);
        Assert.True(v.Validate(null, new SegurancaOptions { Pepper = Base64Url.EncodeToString(new byte[16]) }).Failed);
    }

    [Fact]
    public void Playout_token_exige_32_caracteres_e_recusa_marcadores()
    {
        var v = new PlayoutOptionsValidator();
        Assert.True(v.Validate(null, new PlayoutOptions { Token = new string('a', 64) }).Succeeded);
        Assert.True(v.Validate(null, new PlayoutOptions { Token = new string('a', 31) }).Failed);
        Assert.True(v.Validate(null, new PlayoutOptions { Token = "__GERAR__" }).Failed);
        Assert.True(v.Validate(null, new PlayoutOptions { Token = null }).Failed);
    }
}
