using System.Net;

namespace WebRadio.Application.Abstracoes;

/// <summary>
/// Identidade anônima sem PII (LGPD): HMAC-SHA256(Seguranca__Pepper, valor). O banco nunca guarda IP nem listenerId em claro.
/// </summary>
public interface IListenerIdentity
{
    /// <summary>HMAC do X-Listener-Id (32 bytes).</summary>
    byte[] DeviceHash(Guid listenerId);

    /// <summary>HMAC do IP (32 bytes). IPv6 é normalizado para /64; IPv4 mapeado em IPv6 vira IPv4.</summary>
    byte[] IpHash(IPAddress ip);

    /// <summary>Nome do grupo do SignalR do dispositivo: listener:{hex}.</summary>
    string GrupoListener(Guid listenerId);
}
