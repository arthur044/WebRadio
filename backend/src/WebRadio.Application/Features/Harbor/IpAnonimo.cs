using System.Net;
using System.Net.Sockets;

namespace WebRadio.Application.Features.Harbor;

/// <summary>IP para log, como o nginx: /24 no IPv4 e /64 no IPv6. O IP inteiro nunca vai para o log.</summary>
public static class IpAnonimo
{
    public static string De(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork) return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";

        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }
}
