using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Opcoes;

namespace WebRadio.Infrastructure.Seguranca;

public sealed class ListenerIdentityService(IOptions<SegurancaOptions> opcoes) : IListenerIdentity
{
    private readonly byte[] _pepper = opcoes.Value.PepperBytes();

    public byte[] DeviceHash(Guid listenerId) => Hmac("device:", listenerId.ToByteArray());

    public byte[] IpHash(IPAddress ip) => Hmac("ip:", Normalizar(ip));

    public string GrupoListener(Guid listenerId) => $"listener:{Convert.ToHexString(DeviceHash(listenerId))}";

    /// <summary>IPv4 mapeado em IPv6 vira IPv4; IPv6 puro mantém só o /64 (um /64 inteiro é um único assinante).</summary>
    private static byte[] Normalizar(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Array.Clear(bytes, 8, 8);
        }

        return bytes;
    }

    private byte[] Hmac(string dominio, byte[] valor)
    {
        var entrada = new byte[dominio.Length + valor.Length];
        Encoding.ASCII.GetBytes(dominio, entrada);
        valor.CopyTo(entrada, dominio.Length);
        return HMACSHA256.HashData(_pepper, entrada);
    }
}
