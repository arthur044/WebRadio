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

    /// <summary>Teto do atraso progressivo (D21). O atraso vale só para o par (conta, IP) que já falhou; nunca para a conta inteira.</summary>
    public static readonly TimeSpan AtrasoMaximo = TimeSpan.FromSeconds(2);
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

        // O alerta/atraso nasce do contador da CONTA, mas só paga o atraso quem já falhou nesta conta (o par). Atraso para a conta
        // inteira deixaria um atacante com vários IPs travar o login da vítima e do Admin (DoS de disponibilidade).
        var atraso = TimeSpan.Zero;
        if (par is { Falhas: > 0 } && _contas.TryGetValue(emailNormalizado, out var conta) && agora - conta.InicioDaJanelaUtc < JanelaDaConta
            && conta.Falhas >= FalhasPorContaParaAtraso)
            atraso = TimeSpan.FromMilliseconds(Math.Min(AtrasoMaximo.TotalMilliseconds, (conta.Falhas - FalhasPorContaParaAtraso + 1) * 100));

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

    public Task DesbloquearContaAsync(string emailNormalizado, CancellationToken ct)
    {
        // Ação rara do Admin: varrer os pares é O(n), fora do caminho do login. A chave do par é "<conta>|<ipHash em hex>" e
        // o hex não tem "|": o prefixo "<conta>|" só casa com pares desta conta ("a@x" não desbloqueia "a@x.com").
        var prefixo = emailNormalizado + "|";
        foreach (var chave in _pares.Keys.Where(k => k.StartsWith(prefixo, StringComparison.Ordinal)).ToList()) Remover(_pares, chave);
        Remover(_contas, emailNormalizado);
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
            // Só pares NÃO bloqueados no 1º passo (os mais antigos primeiro): um bloqueio ativo é o que o atacante quer ver
            // solto, e descartar por antiguidade pura destrancaria a vítima assim que uma enxurrada de e-mails inventados chegasse.
            bool Bloqueado(Par p) => p.BloqueadoAte is { } ate && ate > agora;
            foreach (var k in _pares.Where(x => !Bloqueado(x.Value)).OrderBy(x => x.Value.UltimaEmUtc).Take(descartar).Select(x => x.Key).ToList())
                Remover(_pares, k);
            if (Entradas >= LimiteDeEntradas)
                foreach (var k in _contas.OrderBy(x => x.Value.InicioDaJanelaUtc).Take(descartar).Select(x => x.Key).ToList()) Remover(_contas, k);
            // Último recurso (só bloqueados sobraram e o teto segue estourado): sai o bloqueio mais antigo, para a memória não crescer.
            if (Entradas >= LimiteDeEntradas)
                foreach (var k in _pares.OrderBy(x => x.Value.UltimaEmUtc).Take(Entradas - LimiteDeEntradas + 1).Select(x => x.Key).ToList()) Remover(_pares, k);
        }
    }
}
