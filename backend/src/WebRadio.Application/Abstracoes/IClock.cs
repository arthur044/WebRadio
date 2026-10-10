namespace WebRadio.Application.Abstracoes;

/// <summary>D8: todo instante de negócio é UTC. Nada na Application chama DateTime.UtcNow direto, para os testes controlarem o relógio.</summary>
public interface IClock
{
    DateTime UtcAgora { get; }
}
