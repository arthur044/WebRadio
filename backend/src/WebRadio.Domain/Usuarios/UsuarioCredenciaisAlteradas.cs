using WebRadio.Domain.Common;

namespace WebRadio.Domain.Usuarios;

/// <summary>
/// D22: despachado por um SaveChangesInterceptor (Infrastructure) antes do commit. O handler
/// (Application) revoga as famílias de refresh token na mesma transação e grava no outbox o evento
/// 'UsuarioSessoesRevogadas' para o OutboxDispatcher derrubar as conexões do hub. Nunca chamar o SignalR
/// direto a partir de um handler de domínio.
/// </summary>
public sealed record UsuarioCredenciaisAlteradas(Guid UsuarioId, MotivoAlteracaoCredenciais Motivo) : IDomainEvent;
