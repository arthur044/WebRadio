using System.Collections.Concurrent;
using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Opcoes;

namespace WebRadio.Infrastructure.Tests.Suporte;

public sealed class RelogioFalso(DateTime inicio) : IClock
{
    public DateTime UtcAgora { get; private set; } = DateTime.SpecifyKind(inicio, DateTimeKind.Utc);

    public void Avancar(TimeSpan tempo) => UtcAgora = UtcAgora.Add(tempo);
}

/// <summary>Throttle em memória com a mesma semântica do Redis (contador de janela fixa).</summary>
public sealed class ThrottleEmMemoria : IRequestThrottle
{
    private readonly ConcurrentDictionary<string, long> _contadores = new();

    public bool Indisponivel { get; set; }

    /// <summary>Quantas operacoes foram tentadas (inclusive as que falharam).</summary>
    public int Chamadas { get; private set; }

    public Task<bool> TentarReservarAsync(string chave, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        Task.FromResult(Falhar(() => _contadores.TryAdd(chave, 1)));

    public Task<long> IncrementarAsync(string chave, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        Task.FromResult(Falhar(() => _contadores.AddOrUpdate(chave, 1, (_, v) => v + 1)));

    public Task<long> LerAsync(string chave, CancellationToken cancellationToken = default) =>
        Task.FromResult(Falhar(() => _contadores.GetValueOrDefault(chave)));

    public Task RemoverAsync(string chave, CancellationToken cancellationToken = default)
    {
        Falhar(() => _contadores.TryRemove(chave, out _));
        return Task.CompletedTask;
    }

    public Task<TimeSpan?> TempoRestanteAsync(string chave, CancellationToken cancellationToken = default) =>
        Task.FromResult<TimeSpan?>(null);

    private T Falhar<T>(Func<T> operacao)
    {
        Chamadas++;
        return Indisponivel ? throw new InvalidOperationException("Redis fora do ar (simulado)") : operacao();
    }
}

public static class Segredos
{
    public static string ChaveBase64Url(int bytes = 32) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    public static IOptions<SegurancaOptions> Seguranca(string? pepper = null) =>
        Options.Create(new SegurancaOptions { Pepper = pepper ?? ChaveBase64Url() });

    public static IOptions<JwtOptions> Jwt(string? chave = null) =>
        Options.Create(new JwtOptions { SigningKey = chave ?? ChaveBase64Url() });
}
