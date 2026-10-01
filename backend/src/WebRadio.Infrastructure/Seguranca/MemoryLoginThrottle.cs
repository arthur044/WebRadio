using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// D21 em memória (modo degradado do S-A12; o Redis entra com a F09). Por (conta, IpHash): 5 falhas bloqueiam por
/// 15 min. Por conta: a partir de 50 falhas/h só há alerta e atraso progressivo, sem bloquear (senão qualquer
/// pessoa que saiba o e-mail do Admin o mantém trancado para sempre).
/// <para>
/// ATENÇÃO: o estado vive na memória DESTA instância, assim como os limiters do ASP.NET. Com 2+ réplicas da API cada
/// uma conta o próprio limite (N réplicas = N vezes mais tentativas). NÃO escalar a API além de 1 réplica antes da
/// F09 (Redis).
/// </para>
/// </summary>
public sealed class MemoryLoginThrottle(IClock clock, IIpHasher pseudonimizador, ILogger<MemoryLoginThrottle> logger) : ILoginThrottle
{
    public const int FalhasParaBloquear = 5;
    public const int FalhasPorContaParaAtraso = 50;
    public static readonly TimeSpan DuracaoDoBloqueio = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan JanelaDaConta = TimeSpan.FromHours(1);
    private static readonly TimeSpan IntervaloDeLimpeza = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan IntervaloDeLimpezaCheio = TimeSpan.FromSeconds(1);

    /// <summary>Teto duro de entradas (pares + contas): a memória não cresce com e-mails inventados.</summary>
    public const int LimiteDeEntradas = 50_000;

    private int _entradas;
    private DateTime _ultimaLimpezaUtc = DateTime.MinValue;

    /// <summary>Quantidade aproximada de entradas (pares + contas). Barata: ConcurrentDictionary.Count trava tudo.</summary>
    public int Entradas => Volatile.Read(ref _entradas);

    private void Remover<T>(ConcurrentDictionary<string, T> mapa, string chave)
    {
        if (mapa.TryRemove(chave, out _)) Interlocked.Decrement(ref _entradas);
    }
    private readonly object _trava = new();

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

        var par = _pares.GetOrAdd(ChavePar(emailNormalizado, ipHash), _ => { Interlocked.Increment(ref _entradas); return new Par(); });
        lock (par)
        {
            if (par.BloqueadoAte is { } ate && ate <= agora) { par.Falhas = 0; par.BloqueadoAte = null; }
            par.UltimaEmUtc = agora;
            if (++par.Falhas >= FalhasParaBloquear) par.BloqueadoAte = agora + DuracaoDoBloqueio;
        }

        var conta = _contas.GetOrAdd(emailNormalizado, _ => { Interlocked.Increment(ref _entradas); return new Conta { InicioDaJanelaUtc = agora }; });
        int total;
        lock (conta)
        {
            if (agora - conta.InicioDaJanelaUtc >= JanelaDaConta) { conta.Falhas = 0; conta.InicioDaJanelaUtc = agora; }
            total = ++conta.Falhas;
        }

        if (total == FalhasPorContaParaAtraso)
        {
            // Alerta sem PII: HMAC (com o pepper) do e-mail, que permite correlacionar sem expor o endereço
            // nem permitir reverter o prefixo por dicionário de e-mails.
            var id = pseudonimizador.Pseudonimo(emailNormalizado);
            logger.LogWarning("Possível ataque de força bruta: {Falhas} falhas de login em 1 h na conta {ContaId}", total, id);
        }

        return Task.CompletedTask;
    }

    public Task LimparAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct)
    {
        // Só zera o par: o contador por conta continua, para um atacante não zerá-lo logando na própria conta.
        Remover(_pares, ChavePar(emailNormalizado, ipHash));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Limpeza por TEMPO (1x/min), não por falha: custo O(n) fora do caminho quente. Cheio, roda no máximo 1x/s e,
    /// se ainda estiver acima do teto, descarta os ~10% mais antigos, em vez de bloquear ou crescer sem limite.
    /// </summary>
    private void Limpar(DateTime agora)
    {
        var cheio = Entradas >= LimiteDeEntradas;
        if (agora - _ultimaLimpezaUtc < (cheio ? IntervaloDeLimpezaCheio : IntervaloDeLimpeza)) return;
        lock (_trava)
        {
            if (agora - _ultimaLimpezaUtc < (cheio ? IntervaloDeLimpezaCheio : IntervaloDeLimpeza)) return;
            _ultimaLimpezaUtc = agora;

            foreach (var (k, v) in _pares) if (agora - v.UltimaEmUtc > DuracaoDoBloqueio) Remover(_pares, k);
            foreach (var (k, v) in _contas) if (agora - v.InicioDaJanelaUtc > JanelaDaConta) Remover(_contas, k);

            var excesso = Entradas - LimiteDeEntradas;
            if (excesso < 0) return;
            var descartar = excesso + LimiteDeEntradas / 10;
            foreach (var k in _pares.OrderBy(x => x.Value.UltimaEmUtc).Take(descartar).Select(x => x.Key).ToList()) Remover(_pares, k);
            if (Entradas >= LimiteDeEntradas)
                foreach (var k in _contas.OrderBy(x => x.Value.InicioDaJanelaUtc).Take(descartar).Select(x => x.Key).ToList()) Remover(_contas, k);
        }
    }
}
