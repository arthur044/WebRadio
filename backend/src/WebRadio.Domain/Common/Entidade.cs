namespace WebRadio.Domain.Common;

public abstract class Entidade
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public Guid Id { get; protected set; }

    public byte[] RowVersion { get; protected set; } = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void AddDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    protected Entidade(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("O Id não pode ser Guid.Empty.", nameof(id));
        }

        Id = id;
    }

    // Construtor exigido pelo EF Core para materializar entidades existentes.
    protected Entidade()
    {
    }
}
