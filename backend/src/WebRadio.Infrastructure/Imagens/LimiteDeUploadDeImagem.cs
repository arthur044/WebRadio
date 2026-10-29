using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Seguranca;

namespace WebRadio.Infrastructure.Imagens;

/// <summary>
/// Janela fixa de 1 h por Admin (<c>INCR</c> com TTL na primeira contagem). A chave leva o HMAC do id da conta (<c>ChaveOpaca</c>), não o id. Redis
/// fora: conta na memória desta instância (<see cref="MemoryRequestThrottle"/>), com aviso só na primeira falha de cada sequência.
/// </summary>
public sealed class LimiteDeUploadDeImagem(IRequestThrottle contadores, IIpHasher hasher, IClock clock, ILogger<LimiteDeUploadDeImagem> logger) : ILimiteDeUploadDeImagem
{
    public static readonly TimeSpan Janela = TimeSpan.FromHours(1);

    private readonly MemoryRequestThrottle _memoria = new(clock);
    private int _redisFora;

    public async Task<TimeSpan?> RegistrarTentativaAsync(Guid adminId, CancellationToken ct)
    {
        var chave = $"throttle:divimg:{hasher.ChaveOpaca("divimg:" + adminId.ToString("N"))}";
        IRequestThrottle alvo = contadores;
        long n;
        try
        {
            n = await alvo.IncrementarAsync(chave, Janela, ct);
            Interlocked.Exchange(ref _redisFora, 0);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (Interlocked.Exchange(ref _redisFora, 1) == 0)
                logger.LogWarning("DIVULGACAO_UPLOAD_REDIS_INDISPONIVEL ({Tipo}): limite de envio de imagem na memória desta instância.", e.GetType().Name);
            alvo = _memoria;
            n = await alvo.IncrementarAsync(chave, Janela, ct);
        }

        if (n <= ILimiteDeUploadDeImagem.PorHora) return null;
        return await alvo.TempoRestanteAsync(chave, ct) is { } restante && restante > TimeSpan.Zero ? restante : Janela;
    }
}
