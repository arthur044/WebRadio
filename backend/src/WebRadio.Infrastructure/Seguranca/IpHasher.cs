using System.Net;
using System.Security.Cryptography;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Seguranca;

public sealed class IpHasher(byte[] pepper) : IIpHasher
{
    public byte[] Hash(IPAddress? ip)
    {
        if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
        // Sem IP (não acontece com socket real): hash de um marcador fixo, nunca string vazia.
        var bytes = ip?.GetAddressBytes() ?? "desconhecido"u8.ToArray();
        return HMACSHA256.HashData(pepper, bytes);
    }
}
