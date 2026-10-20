using WebRadio.Domain.Common;

namespace WebRadio.Application.Abstracoes;

/// <summary>
/// D22: reage a um evento de domínio ANTES do commit, no mesmo <c>SaveChanges</c> que o levantou. O que o handler altera no
/// DbContext do escopo é gravado junto: ou tudo, ou nada. Sem efeito fora do banco aqui (o retry do EF reexecuta o bloco).
/// </summary>
public interface IDomainEventHandler<in TEvento> where TEvento : IDomainEvent
{
    Task HandleAsync(TEvento evento, CancellationToken ct);
}
