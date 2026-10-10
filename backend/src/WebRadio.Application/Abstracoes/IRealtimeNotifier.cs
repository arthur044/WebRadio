namespace WebRadio.Application.Abstracoes;

/// <summary>Notificação em tempo real (SignalR). A implementação chega com E1-F10; aqui só o contrato.</summary>
public interface IRealtimeNotifier
{
    Task NotificarTodosAsync(string evento, object? carga, CancellationToken cancellationToken = default);

    /// <summary>Envia para um grupo (locutores, listener:{hash}).</summary>
    Task NotificarGrupoAsync(string grupo, string evento, object? carga, CancellationToken cancellationToken = default);

    /// <summary>Derruba as conexões do usuário (papel alterado, desativação; S-M08).</summary>
    Task DesconectarUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}
