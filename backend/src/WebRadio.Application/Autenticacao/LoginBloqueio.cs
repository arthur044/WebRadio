using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Application.Autenticacao;

/// <summary>
/// D21 / S-A12: 5 falhas por (conta, IpHash) bloqueiam por 15 min; por conta só há contador de alerta (≥ 50/h) com atraso
/// progressivo, sem bloquear. Redis fora do ar → modo degradado: o RateLimiter em memória de /auth/login continua valendo e o
/// evento é registrado no log.
/// </summary>
public sealed class LoginBloqueio(IRequestThrottle throttle, ILogger<LoginBloqueio> logger)
{
    // Escopo = 1 request: depois da 1a falha do Redis, as demais chamadas do mesmo login nem tentam (modo degradado).
    private bool _indisponivel;

    public const int MaximoFalhas = 5;
    public const int LimiteAlertaPorConta = 50;
    public static readonly TimeSpan DuracaoBloqueio = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan JanelaConta = TimeSpan.FromHours(1);
    public static readonly TimeSpan AtrasoMaximo = TimeSpan.FromSeconds(2);

    public async Task<bool> EstaBloqueadoAsync(string emailNormalizado, byte[]? ipHash, CancellationToken cancellationToken)
    {
        if (_indisponivel)
        {
            return false;
        }

        try
        {
            return await throttle.LerAsync(ChaveBloqueio(emailNormalizado, ipHash), cancellationToken) > 0
                || await throttle.LerAsync(ChaveFalhas(emailNormalizado, ipHash), cancellationToken) >= MaximoFalhas;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _indisponivel = true;
            logger.LogWarning(ex, "REDIS_INDISPONIVEL: bloqueio de login por (conta, IP) ignorado (modo degradado)");
            return false;
        }
    }

    /// <summary>Registra uma falha e devolve o atraso a aplicar (progressivo depois do limite de alerta por conta).</summary>
    public async Task<TimeSpan> RegistrarFalhaAsync(string emailNormalizado, byte[]? ipHash, CancellationToken cancellationToken)
    {
        if (_indisponivel)
        {
            return TimeSpan.Zero;
        }

        try
        {
            var falhas = await throttle.IncrementarAsync(ChaveFalhas(emailNormalizado, ipHash), DuracaoBloqueio, cancellationToken);
            if (falhas >= MaximoFalhas)
            {
                // O contador tem janela fixa desde a 1a falha; o bloqueio de 15 min conta a partir da 5a.
                await throttle.TentarReservarAsync(ChaveBloqueio(emailNormalizado, ipHash), DuracaoBloqueio, cancellationToken);
            }

            var porConta = await throttle.IncrementarAsync(ChaveConta(emailNormalizado), JanelaConta, cancellationToken);

            if (porConta < LimiteAlertaPorConta)
            {
                return TimeSpan.Zero;
            }

            logger.LogWarning("ALERTA_FORCA_BRUTA: {Falhas} falhas de login na última hora para uma mesma conta", porConta);
            var atraso = TimeSpan.FromMilliseconds(100 * (porConta - LimiteAlertaPorConta + 1));
            return atraso > AtrasoMaximo ? AtrasoMaximo : atraso;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _indisponivel = true;
            logger.LogWarning(ex, "REDIS_INDISPONIVEL: falha de login não contabilizada (modo degradado)");
            return TimeSpan.Zero;
        }
    }

    public async Task LimparAsync(string emailNormalizado, byte[]? ipHash, CancellationToken cancellationToken)
    {
        if (_indisponivel)
        {
            return;
        }

        try
        {
            await throttle.RemoverAsync(ChaveFalhas(emailNormalizado, ipHash), cancellationToken);
            await throttle.RemoverAsync(ChaveBloqueio(emailNormalizado, ipHash), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _indisponivel = true;
            logger.LogWarning(ex, "REDIS_INDISPONIVEL: contador de falhas não limpo (modo degradado)");
        }
    }

    // O e-mail nunca vira chave em claro (PII): SHA-256 do e-mail normalizado.
    private static string ChaveConta(string emailNormalizado) => $"login:conta:{HashHex(emailNormalizado)}";

    private static string ChaveBloqueio(string emailNormalizado, byte[]? ipHash) =>
        $"login:bloqueio:{HashHex(emailNormalizado)}:{(ipHash is null ? "sem-ip" : Convert.ToHexString(ipHash))}";

    private static string ChaveFalhas(string emailNormalizado, byte[]? ipHash) =>
        $"login:falhas:{HashHex(emailNormalizado)}:{(ipHash is null ? "sem-ip" : Convert.ToHexString(ipHash))}";

    private static string HashHex(string valor) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(valor)));
}
