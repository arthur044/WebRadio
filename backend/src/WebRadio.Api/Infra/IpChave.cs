using System.Net;
using System.Net.Sockets;

namespace WebRadio.Api.Infra;

/// <summary>Chave de partição por IP dos limiters (global e de login): IPv4 normalizado e IPv6 agrupado por /64.</summary>
public static class IpChave
{
    public static string De(IPAddress? ip)
    {
        if (ip is null) return "desconhecido";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();
        // IPv6: um cliente controla um /64 inteiro; particionar por endereço completo o deixaria burlar o limite.
        return Convert.ToHexString(ip.GetAddressBytes().AsSpan(0, 8));
    }
}
