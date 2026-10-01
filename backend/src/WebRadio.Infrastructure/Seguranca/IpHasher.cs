using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Seguranca;

public sealed class IpHasher(byte[] pepper) : IIpHasher
{
    public byte[] Hash(IPAddress? ip)
    {
        if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
        // Sem IP (não acontece com socket real): hash de um marcador fixo, nunca string vazia.
        var bytes = ip is null ? "desconhecido"u8.ToArray() : ip.AddressFamily == AddressFamily.InterNetworkV6
            ? ip.GetAddressBytes().AsSpan(0, 8).ToArray()  // /64, igual à chave dos limiters (IpChave)
            : ip.GetAddressBytes();
        return HMACSHA256.HashData(pepper, bytes);
    }

    public string Pseudonimo(string valor)
        => Convert.ToHexString(HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(valor)))[..8];
}
