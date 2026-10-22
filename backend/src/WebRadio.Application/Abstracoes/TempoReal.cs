using System.Net;

namespace WebRadio.Application.Abstracoes;

/// <summary>Notificação em tempo real (SignalR, E1-F10). Os handlers não chamam isto direto: gravam no outbox (D22).</summary>
public interface IRealtimeNotifier
{
    Task NotificarTodosAsync(string evento, object? carga, CancellationToken ct = default);

    /// <summary>
    /// Envia para um ou mais grupos (<see cref="GruposDoHub.Locutores"/>, <c>listener:{hash}</c>). Quem está em mais de um
    /// recebe uma vez só.
    /// </summary>
    Task NotificarGruposAsync(IReadOnlyList<string> grupos, string evento, object? carga, CancellationToken ct = default);

    /// <summary>Derruba as conexões do usuário (papel alterado, desativação; S-M08).</summary>
    Task DesconectarUsuarioAsync(Guid usuarioId, CancellationToken ct = default);
}

public static class GruposDoHub
{
    public const string Locutores = "locutores";

    /// <summary>Todo JWT com <c>role = Admin</c> (e sem o token restrito de troca de senha): alertas administrativos (E2-F12).</summary>
    public const string Admins = "admins";

    /// <summary>Todo JWT autenticado, a partir do <c>sub</c>: eventos que só o próprio usuário recebe (E2-F12).</summary>
    public static string Usuario(Guid usuarioId) => $"usuario:{usuarioId:D}";
}

/// <summary>
/// Identidade anônima sem PII (LGPD): HMAC-SHA256(Seguranca__Pepper, valor). O banco nunca guarda IP nem listenerId em claro.
/// </summary>
public interface IListenerIdentity
{
    /// <summary>HMAC do X-Listener-Id (32 bytes).</summary>
    byte[] DeviceHash(Guid listenerId);

    /// <summary>HMAC do IP (32 bytes), o mesmo do <see cref="IIpHasher"/>: IPv6 pelo /64, IPv4 mapeado vira IPv4.</summary>
    byte[] IpHash(IPAddress? ip);

    /// <summary>Nome do grupo do SignalR do dispositivo: <c>listener:{hex}</c>.</summary>
    string GrupoListener(Guid listenerId);
}

/// <summary>
/// Primitivas atômicas de throttle (Redis: SET NX EX e INCR). Falha de infraestrutura propaga como exceção; quem chama
/// decide o modo degradado (D6).
/// </summary>
public interface IRequestThrottle
{
    /// <summary>SET chave NX EX ttl: true se a chave foi criada agora, false se já existia.</summary>
    Task<bool> TentarReservarAsync(string chave, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>INCR chave; o TTL só é definido na primeira contagem (janela fixa). Devolve o valor depois do incremento.</summary>
    Task<long> IncrementarAsync(string chave, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Valor atual do contador, 0 se não existir.</summary>
    Task<long> LerAsync(string chave, CancellationToken ct = default);

    Task RemoverAsync(string chave, CancellationToken ct = default);

    /// <summary>Tempo restante da chave, null se não existir ou não expirar.</summary>
    Task<TimeSpan?> TempoRestanteAsync(string chave, CancellationToken ct = default);
}
