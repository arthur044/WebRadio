using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// Mesmo contrato do <see cref="RedisRequestThrottle"/>, na memória DESTA instância: só para Development sem Redis
/// (fora dele a Api exige Redis:Host). Um lock simples basta: não é caminho quente em dev.
/// </summary>
public sealed class MemoryRequestThrottle(IClock clock) : IRequestThrottle
{
    /// <summary>Teto de chaves: ao estourar, as vencidas saem; se ainda assim não couber, a operação falha (quem chama degrada).</summary>
    public const int LimiteDeChaves = 50_000;

    private readonly Dictionary<string, (long Valor, DateTime ExpiraEmUtc)> _chaves = new(StringComparer.Ordinal);
    private readonly Lock _trava = new();

    public Task<bool> TentarReservarAsync(string chave, TimeSpan ttl, CancellationToken ct = default)
    {
        lock (_trava)
        {
            if (Viva(chave) is not null) return Task.FromResult(false);
            Gravar(chave, 1, clock.UtcNow.Add(ttl));
            return Task.FromResult(true);
        }
    }

    public Task<long> IncrementarAsync(string chave, TimeSpan ttl, CancellationToken ct = default)
    {
        lock (_trava)
        {
            var atual = Viva(chave);
            var novo = (atual?.Valor ?? 0) + 1;
            Gravar(chave, novo, atual?.ExpiraEmUtc ?? clock.UtcNow.Add(ttl));
            return Task.FromResult(novo);
        }
    }

    public Task<long> LerAsync(string chave, CancellationToken ct = default)
    {
        lock (_trava) return Task.FromResult(Viva(chave)?.Valor ?? 0);
    }

    public Task RemoverAsync(string chave, CancellationToken ct = default)
    {
        lock (_trava) _chaves.Remove(chave);
        return Task.CompletedTask;
    }

    public Task<TimeSpan?> TempoRestanteAsync(string chave, CancellationToken ct = default)
    {
        lock (_trava) return Task.FromResult(Viva(chave) is { } v ? v.ExpiraEmUtc - clock.UtcNow : (TimeSpan?)null);
    }

    private (long Valor, DateTime ExpiraEmUtc)? Viva(string chave)
    {
        if (!_chaves.TryGetValue(chave, out var v)) return null;
        if (v.ExpiraEmUtc > clock.UtcNow) return v;
        _chaves.Remove(chave);
        return null;
    }

    private void Gravar(string chave, long valor, DateTime expira)
    {
        if (_chaves.Count >= LimiteDeChaves && !_chaves.ContainsKey(chave))
        {
            var agora = clock.UtcNow;
            foreach (var vencida in _chaves.Where(p => p.Value.ExpiraEmUtc <= agora).Select(p => p.Key).ToList()) _chaves.Remove(vencida);
            if (_chaves.Count >= LimiteDeChaves) throw new InvalidOperationException("Throttle em memória cheio.");
        }

        _chaves[chave] = (valor, expira);
    }
}
