using System.Net;
using System.Security.Cryptography;
using System.Text;
using WebRadio.Infrastructure.Seguranca;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>#17: o pepper nunca é usado direto; cada uso tem a sua subchave HKDF e o seu prefixo de domínio no HMAC.</summary>
public class SubchavesDoPepperTests
{
    private static readonly byte[] Pepper = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    // Vetores calculados à parte (HKDF-SHA256 de RFC 5869 em Python): ikm = pepper, salt = zeros, info = "webradio/pepper/v1/<uso>", 32 bytes.
    [Theory]
    [InlineData("ip", "0c6ca7719d7b80bfad311e280b4122e10af9233fc80a3786c492210da33b2b26")]
    [InlineData("pseudonimo", "8c360da4fb8f4deeb051bfb755b2ddccc5c11a9f9327dc3fdec42bb268223a51")]
    [InlineData("chave", "98707edbf3b1075b886e6d1199fbea4f0b7643b73c7cefb0297217e4ad1e0655")]
    [InlineData("device", "747007b7661fe52f4439a10f4f299a2e599177baaf93eb491bf420e5606780d5")]
    [InlineData("refresh", "b94e744be8dae4fde83c671dac1d4cb1872fbc168914e72654ce99f362aa3704")]
    public void A_subchave_bate_com_o_vetor_de_referencia(string uso, string esperadoHex)
    {
        Assert.Equal(esperadoHex, Convert.ToHexString(SubchavesDoPepper.Derivar(Pepper, uso)).ToLowerInvariant());
    }

    [Fact]
    public void Cada_uso_tem_a_sua_subchave_e_nenhuma_e_o_pepper()
    {
        var chaves = new[] { SubchavesDoPepper.Ip, SubchavesDoPepper.Pseudonimo, SubchavesDoPepper.Chave, SubchavesDoPepper.Dispositivo, SubchavesDoPepper.Refresh }
            .Select(u => Convert.ToHexString(SubchavesDoPepper.Derivar(Pepper, u))).ToList();

        Assert.Equal(5, chaves.Distinct().Count());
        Assert.DoesNotContain(Convert.ToHexString(Pepper), chaves);
        Assert.All(chaves, c => Assert.Equal(64, c.Length));
        Assert.NotEqual(SubchavesDoPepper.Derivar(Pepper, "ip"), SubchavesDoPepper.Derivar(Enumerable.Repeat((byte)9, 32).ToArray(), "ip")); // outro pepper, outra subchave
    }

    [Fact]
    public void Uso_desconhecido_ou_vazio_e_recusado()
    {
        Assert.Throws<ArgumentException>(() => SubchavesDoPepper.Derivar(Pepper, ""));
        Assert.Throws<ArgumentException>(() => SubchavesDoPepper.Derivar(Pepper, "   "));
    }

    [Fact]
    public void O_hash_do_IP_nao_e_mais_o_HMAC_do_pepper_cru_e_o_mesmo_texto_nao_coincide_entre_usos()
    {
        var h = new IpHasher(Pepper);
        var ip = IPAddress.Parse("203.0.113.7");

        Assert.NotEqual(HMACSHA256.HashData(Pepper, ip.GetAddressBytes()), h.Hash(ip)); // não é o HMAC direto do pepper

        // O mesmo texto em usos diferentes (chave opaca × pseudônimo × IP em texto) nunca coincide.
        var chave = h.ChaveOpaca("203.0.113.7");
        var pseudo = h.Pseudonimo("203.0.113.7");
        Assert.NotEqual(chave[..8], pseudo);
        Assert.NotEqual(Convert.ToHexString(h.Hash(ip)), chave);
    }

    [Fact]
    public void Chave_opaca_e_pseudonimo_continuam_estaveis_para_o_mesmo_pepper_e_mudam_com_outro()
    {
        var a = new IpHasher(Pepper);
        var b = new IpHasher(Pepper);
        var outro = new IpHasher(Enumerable.Repeat((byte)9, 32).ToArray());

        Assert.Equal(a.ChaveOpaca("x"), b.ChaveOpaca("x"));
        Assert.Equal(a.Pseudonimo("x"), b.Pseudonimo("x"));
        Assert.NotEqual(a.ChaveOpaca("x"), outro.ChaveOpaca("x"));
        Assert.Equal(64, a.ChaveOpaca("x").Length);
        Assert.Equal(8, a.Pseudonimo("x").Length);
    }

    [Fact]
    public void O_hash_de_dispositivo_usa_a_subchave_device_com_o_prefixo_de_dominio()
    {
        var listener = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var esperado = HMACSHA256.HashData(SubchavesDoPepper.Derivar(Pepper, SubchavesDoPepper.Dispositivo), (byte[])[.. "device:"u8.ToArray(), .. listener.ToByteArray()]);

        var obtido = new ListenerIdentity(Pepper, new IpHasher(Pepper)).DeviceHash(listener);

        Assert.Equal(esperado, obtido);
        Assert.NotEqual(HMACSHA256.HashData(Pepper, (byte[])[.. "device:"u8.ToArray(), .. listener.ToByteArray()]), obtido);
    }

    [Fact]
    public void O_hash_do_IP_usa_a_subchave_ip_com_o_prefixo_ip()
    {
        var ip = IPAddress.Parse("203.0.113.7");
        var esperado = HMACSHA256.HashData(SubchavesDoPepper.Derivar(Pepper, SubchavesDoPepper.Ip), (byte[])[.. "ip:"u8.ToArray(), .. ip.GetAddressBytes()]);

        Assert.Equal(esperado, new IpHasher(Pepper).Hash(ip));
    }

    [Fact]
    public void Chave_opaca_e_pseudonimo_usam_as_subchaves_e_os_prefixos_de_dominio()
    {
        var h = new IpHasher(Pepper);

        Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(SubchavesDoPepper.Derivar(Pepper, SubchavesDoPepper.Chave), Encoding.UTF8.GetBytes("chave:abc"))), h.ChaveOpaca("abc"));
        Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(SubchavesDoPepper.Derivar(Pepper, SubchavesDoPepper.Pseudonimo), Encoding.UTF8.GetBytes("pseudonimo:abc")))[..8], h.Pseudonimo("abc"));
    }
}
