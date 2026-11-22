using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// HMAC-SHA256 com subchaves por uso (HKDF do pepper, #17) e prefixo de domínio no que é assinado: o IP, a chave opaca e o pseudônimo de log nunca compartilham chave nem mensagem.
/// Trocar o esquema zera os hashes de IP e os contadores que dependem deles (aceito antes de produção).
/// </summary>
public sealed class IpHasher : IIpHasher
{
    private static readonly byte[] PrefixoIp = "ip:"u8.ToArray();

    private readonly byte[] _chaveDoIp;
    private readonly byte[] _chaveOpaca;
    private readonly byte[] _chaveDoPseudonimo;

    public IpHasher(byte[] pepper)
    {
        _chaveDoIp = SubchavesDoPepper.Derivar(pepper, SubchavesDoPepper.Ip);
        _chaveOpaca = SubchavesDoPepper.Derivar(pepper, SubchavesDoPepper.Chave);
        _chaveDoPseudonimo = SubchavesDoPepper.Derivar(pepper, SubchavesDoPepper.Pseudonimo);
    }

    public byte[] Hash(IPAddress? ip)
    {
        if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
        // Sem IP (não acontece com socket real): hash de um marcador fixo, nunca string vazia.
        var bytes = ip is null ? "desconhecido"u8.ToArray() : ip.AddressFamily == AddressFamily.InterNetworkV6
            ? ip.GetAddressBytes().AsSpan(0, 8).ToArray()  // /64, igual à chave dos limiters (IpChave)
            : ip.GetAddressBytes();
        return HMACSHA256.HashData(_chaveDoIp, (byte[])[.. PrefixoIp, .. bytes]);
    }

    public string ChaveOpaca(string valor)
        => Convert.ToHexString(HMACSHA256.HashData(_chaveOpaca, Encoding.UTF8.GetBytes("chave:" + valor)));

    public string Pseudonimo(string valor)
        => Convert.ToHexString(HMACSHA256.HashData(_chaveDoPseudonimo, Encoding.UTF8.GetBytes("pseudonimo:" + valor)))[..8];
}
