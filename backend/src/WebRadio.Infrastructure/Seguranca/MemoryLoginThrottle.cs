using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// D21 em memória (modo degradado do S-A12; o Redis entra com a F09). Por (conta, IpHash): 5 falhas bloqueiam por
/// 15 min. Por conta: a partir de 50 falhas/h só há alerta e atraso progressivo, sem bloquear (senão qualquer
/// pessoa que saiba o e-mail do Admin o mantém trancado para sempre).
/// </summary>
public sealed class MemoryLoginThrottle(IClock clock, ILogger<MemoryLoginThrottle> logger) : ILoginThrottle
{
    public const int FalhasParaBloquear = 5;
    public const int FalhasPorContaParaAtraso = 50;
    public static readonly TimeSpan DuracaoDoBloqueio = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan JanelaDaConta = TimeSpan.FromHours(1);
    private const int LimiteDeEntradas = 50_000;

    private sealed class Par { public int Falhas; public DateTime? BloqueadoAte; public DateTime UltimaEmUtc; }
    private sealed class Conta { public int Falhas; public DateTime InicioDaJanelaUtc; }

    private readonly ConcurrentDictionary<string, Par> _pares = new();
    private readonly ConcurrentDictionary<string, Conta> _contas = new();

    private static string ChavePar(string email, byte[] ipHash) => email + "|" + Convert.ToHexString(ipHash);

    public Task<AvaliacaoDeLogin> AvaliarAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct)
    {
        var agora = clock.UtcNow;
        var bloqueado = _pares.TryGetValue(ChavePar(emailNormalizado, ipHash), out var par)
                        && par.BloqueadoAte is { } ate && ate > agora;

        var atraso = TimeSpan.Zero;
        if (_contas.TryGetValue(emailNormalizado, out var conta) && agora - conta.InicioDaJanelaUtc < JanelaDaConta
            && conta.Falhas >= FalhasPorContaParaAtraso)
            atraso = TimeSpan.FromMilliseconds(Math.Min(5000, (conta.Falhas - FalhasPorContaParaAtraso + 1) * 100));

        return Task.FromResult(new AvaliacaoDeLogin(bloqueado, atraso));
    }

    public Task RegistrarFalhaAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct)
    {
        var agora = clock.UtcNow;
        Limpar(agora);

        var par = _pares.GetOrAdd(ChavePar(emailNormalizado, ipHash), _ => new Par());
        lock (par)
        {
            if (par.BloqueadoAte is { } ate && ate <= agora) { par.Falhas = 0; par.BloqueadoAte = null; }
            par.UltimaEmUtc = agora;
            if (++par.Falhas >= FalhasParaBloquear) par.BloqueadoAte = agora + DuracaoDoBloqueio;
        }

        var conta = _contas.GetOrAdd(emailNormalizado, _ => new Conta { InicioDaJanelaUtc = agora });
        int total;
        lock (conta)
        {
            if (agora - conta.InicioDaJanelaUtc >= JanelaDaConta) { conta.Falhas = 0; conta.InicioDaJanelaUtc = agora; }
            total = ++conta.Falhas;
        }

        if (total == FalhasPorContaParaAtraso)
        {
            // Alerta sem PII: só um prefixo do SHA-256 do e-mail, que permite correlacionar sem expor o endereço.
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(emailNormalizado)))[..8];
            logger.LogWarning("Possível ataque de força bruta: {Falhas} falhas de login em 1 h na conta {ContaId}", total, id);
        }

        return Task.CompletedTask;
    }

    public Task LimparAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct)
    {
        // Só zera o par: o contador por conta continua, para um atacante não zerá-lo logando na própria conta.
        _pares.TryRemove(ChavePar(emailNormalizado, ipHash), out _);
        return Task.CompletedTask;
    }

    private void Limpar(DateTime agora)
    {
        if (_pares.Count + _contas.Count < LimiteDeEntradas) return;
        foreach (var (k, v) in _pares) if (agora - v.UltimaEmUtc > DuracaoDoBloqueio) _pares.TryRemove(k, out _);
        foreach (var (k, v) in _contas) if (agora - v.InicioDaJanelaUtc > JanelaDaConta) _contas.TryRemove(k, out _);
    }
}
