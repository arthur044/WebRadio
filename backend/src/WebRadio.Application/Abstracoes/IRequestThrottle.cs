namespace WebRadio.Application.Abstracoes;

/// <summary>Primitivas atômicas de throttle (Redis: SET NX EX e INCR). Falha de infraestrutura propaga como exceção; quem chama decide o modo degradado.</summary>
public interface IRequestThrottle
{
    /// <summary>SET chave NX EX ttl: true se a chave foi criada agora, false se já existia.</summary>
    Task<bool> TentarReservarAsync(string chave, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>INCR chave; o TTL só é definido na primeira contagem (janela fixa). Devolve o valor depois do incremento.</summary>
    Task<long> IncrementarAsync(string chave, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>Valor atual do contador, 0 se não existir.</summary>
    Task<long> LerAsync(string chave, CancellationToken cancellationToken = default);

    Task RemoverAsync(string chave, CancellationToken cancellationToken = default);

    /// <summary>Tempo restante da chave, null se não existir ou não expirar.</summary>
    Task<TimeSpan?> TempoRestanteAsync(string chave, CancellationToken cancellationToken = default);
}
