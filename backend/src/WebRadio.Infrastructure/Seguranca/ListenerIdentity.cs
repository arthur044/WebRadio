using System.Net;
using System.Security.Cryptography;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Seguranca;

public sealed class ListenerIdentity(byte[] pepper, IIpHasher ipHasher) : IListenerIdentity
{
    // Prefixo de domínio e subchave própria (HKDF, #17): o HMAC de um listenerId nunca coincide com o de um IP ou de outro valor.
    private static readonly byte[] Dominio = "device:"u8.ToArray();

    private readonly byte[] _chave = SubchavesDoPepper.Derivar(pepper, SubchavesDoPepper.Dispositivo);

    public byte[] DeviceHash(Guid listenerId) => HMACSHA256.HashData(_chave, (byte[])[.. Dominio, .. listenerId.ToByteArray()]);

    public byte[] IpHash(IPAddress? ip) => ipHasher.Hash(ip);

    public string GrupoListener(Guid listenerId) => GruposDoHub.Listener(DeviceHash(listenerId));
}
