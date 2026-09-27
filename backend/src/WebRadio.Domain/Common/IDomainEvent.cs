namespace WebRadio.Domain.Common;

/// <summary>
/// Marcador puro (D22): sem MediatR aqui, senão o Domain passaria a depender da Application e o
/// NetArchTest (E1-F02) reprovaria. O despacho real acontece num SaveChangesInterceptor na Infrastructure,
/// antes do commit, chamando IDomainEventHandler&lt;T&gt; da Application.
/// </summary>
public interface IDomainEvent
{
}
