using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Application.Features.Harbor;

/// <summary>
/// 06 "Throttle do harbor/auth" (desenho do D21). Só as falhas de CREDENCIAL contam (identificador desconhecido ou segredo errado).
/// Bloqueio por par (identificador, IP): 10 falhas em 5 min. Por IP: <see cref="LimitePorIp"/>, contra varredura de identificadores.
/// Por identificador sozinho: só alerta, nunca bloqueio (o identificador trafega em claro). Atrás do proxy nada bloqueia.
/// Alertas (E2-F12, sem bloquear nada): o contador por identificador chegando a <see cref="AlertaPorIdentificador"/> e o contador
/// GLOBAL (toda falha de credencial, conhecida ou não; chave sem identificador nem IP) chegando a <see cref="AlertaGlobal"/>. O INCR
/// atômico devolve cada valor uma vez só por janela, então cada alerta sai no máximo uma vez por chave e janela.
/// As chaves são HMAC (<c>ChaveOpaca</c> e o hash do IP) e só valores já validados entram nelas. Contador fora = segue sem
/// throttle: o segredo tem 256 bits, e recusar tudo tiraria o ao vivo do ar junto com o Redis.
/// </summary>
public sealed class ThrottleDoHarbor(IRequestThrottle contadores, IIpHasher pseudonimizador, IOptions<HarborOptions> opcoes, ILogger<ThrottleDoHarbor> logger)
{
    public const int LimitePorPar = 10;
    public const int LimitePorIp = 50;
    public const int AlertaPorIdentificador = 10;
    public const int AlertaGlobal = 50;
    public static readonly TimeSpan Janela = TimeSpan.FromMinutes(5);

    private bool AtrasDoProxy => opcoes.Value.AtrasDoProxy;

    public async Task<bool> BloqueadoAsync(Guid identificador, byte[] ipHash, CancellationToken ct)
    {
        if (AtrasDoProxy) return false;
        try
        {
            return await contadores.LerAsync(ChavePar(identificador, ipHash), ct) >= LimitePorPar
                || await contadores.LerAsync(ChaveIp(ipHash), ct) >= LimitePorIp;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Indisponivel(ex);
            return false;
        }
    }

    /// <summary>Conta a falha e diz quais alertas ela acabou de disparar; o contador fora devolve o que já tinha disparado (nada, no caso comum).</summary>
    public async Task<AlertasDaFalha> RegistrarFalhaAsync(Guid identificador, byte[] ipHash, CancellationToken ct)
    {
        var alertas = AlertasDaFalha.Nenhum;
        try
        {
            if (!AtrasDoProxy)
            {
                await contadores.IncrementarAsync(ChavePar(identificador, ipHash), Janela, ct);
                await contadores.IncrementarAsync(ChaveIp(ipHash), Janela, ct);
            }

            if (await contadores.IncrementarAsync(ChaveIdentificador(identificador), Janela, ct) == AlertaPorIdentificador)
            {
                alertas |= AlertasDaFalha.Identificador;
                logger.LogWarning("HARBOR_ALERTA_IDENTIFICADOR identificador {Identificador}: {Falhas} falhas de credencial em {Minutos} min",
                    identificador, AlertaPorIdentificador, Janela.TotalMinutes);
            }

            if (await contadores.IncrementarAsync(ChaveGlobal, Janela, ct) == AlertaGlobal)
            {
                alertas |= AlertasDaFalha.Global;
                logger.LogWarning("HARBOR_ALERTA_GLOBAL {Falhas} falhas de credencial em {Minutos} min", AlertaGlobal, Janela.TotalMinutes);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Indisponivel(ex);
        }

        return alertas;
    }

    public async Task LimparAsync(Guid identificador, byte[] ipHash, CancellationToken ct)
    {
        if (AtrasDoProxy) return;
        try
        {
            await contadores.RemoverAsync(ChavePar(identificador, ipHash), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Indisponivel(ex);
        }
    }

    private void Indisponivel(Exception ex) =>
        logger.LogError(ex, "HARBOR_THROTTLE_INDISPONIVEL: o harbor/auth segue sem limite de tentativas");

    private string Id(Guid identificador) => pseudonimizador.ChaveOpaca("harbor:" + identificador.ToString("D"));

    private string ChavePar(Guid identificador, byte[] ipHash) => $"wr:harbor:{{{Id(identificador)}}}:par:{Convert.ToHexString(ipHash)}";

    private static string ChaveIp(byte[] ipHash) => $"wr:harbor:ip:{Convert.ToHexString(ipHash)}";

    private string ChaveIdentificador(Guid identificador) => $"wr:harbor:{{{Id(identificador)}}}:falhas";

    private const string ChaveGlobal = "wr:harbor:global:falhas";
}

/// <summary>O que uma falha de credencial acabou de disparar (<see cref="ThrottleDoHarbor.RegistrarFalhaAsync"/>).</summary>
[Flags]
public enum AlertasDaFalha
{
    Nenhum = 0,

    /// <summary>O contador do identificador chegou a <see cref="ThrottleDoHarbor.AlertaPorIdentificador"/>.</summary>
    Identificador = 1,

    /// <summary>O contador global chegou a <see cref="ThrottleDoHarbor.AlertaGlobal"/>.</summary>
    Global = 2,
}
