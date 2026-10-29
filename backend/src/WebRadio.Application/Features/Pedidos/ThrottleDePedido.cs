using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Pipeline;
using WebRadio.Domain.Erros;

namespace WebRadio.Application.Features.Pedidos;

/// <summary>
/// Disjuntor do Redis do throttle de pedido, como o do <c>LoginThrottleComFallback</c>: na primeira falha abre por 30 s e as
/// chamadas vão direto para o fallback SQL, sem pagar o timeout do <c>StackExchange.Redis</c> a cada pedido. Passados os 30 s fica
/// MEIO-ABERTO: uma única chamada sonda o Redis (as demais seguem no SQL enquanto ela estiver em voo).
/// </summary>
public sealed class DisjuntorDoRedis(IClock clock)
{
    public static readonly TimeSpan TempoAberto = TimeSpan.FromSeconds(30);

    private long _abertoAteTicks; // 0 = fechado
    private int _sondaEmVoo;

    public bool Fechado => Volatile.Read(ref _abertoAteTicks) == 0;

    public PermissaoDoRedis Pedir()
    {
        var ate = Volatile.Read(ref _abertoAteTicks);
        if (ate == 0) return PermissaoDoRedis.Usar;
        if (clock.UtcNow.Ticks < ate) return PermissaoDoRedis.Evitar;
        return Interlocked.CompareExchange(ref _sondaEmVoo, 1, 0) == 0 ? PermissaoDoRedis.Sondar : PermissaoDoRedis.Evitar;
    }

    public void Sucesso(PermissaoDoRedis permissao)
    {
        if (permissao != PermissaoDoRedis.Sondar) return;
        Volatile.Write(ref _abertoAteTicks, 0);
        Volatile.Write(ref _sondaEmVoo, 0);
    }

    /// <summary>Abre (ou reabre) por <see cref="TempoAberto"/>. Devolve <c>true</c> quando o disjuntor estava fechado: quem chama registra o aviso uma vez.</summary>
    public bool Falha(PermissaoDoRedis permissao)
    {
        var estavaFechado = Fechado;
        Volatile.Write(ref _abertoAteTicks, (clock.UtcNow + TempoAberto).Ticks);
        if (permissao == PermissaoDoRedis.Sondar) Volatile.Write(ref _sondaEmVoo, 0);
        return estavaFechado;
    }
}

public enum PermissaoDoRedis
{
    /// <summary>Disjuntor fechado: usa o Redis.</summary>
    Usar,

    /// <summary>Meio-aberto: esta chamada é a sonda.</summary>
    Sondar,

    /// <summary>Aberto: vai para o fallback SQL.</summary>
    Evitar,
}

/// <summary>Teto global de <c>Pendente</c> com cache de 5 s: a contagem roda em todo <c>POST</c> e não precisa ser exata (07, E3-D10).</summary>
public sealed class CacheDoTeto(IClock clock)
{
    public static readonly TimeSpan Validade = TimeSpan.FromSeconds(5);

    private (DateTime ValidoAte, bool Atingido)? _ultimo;

    public async Task<bool> AtingidoAsync(Func<Task<bool>> consultar)
    {
        if (_ultimo is { } u && clock.UtcNow < u.ValidoAte) return u.Atingido;
        var atingido = await consultar();
        _ultimo = (clock.UtcNow + Validade, atingido);
        return atingido;
    }
}

/// <summary>Do <see cref="ThrottleDePedido"/> para o handler no mesmo escopo: o Redis estava fora, então o handler aplica o fallback SQL na transação.</summary>
public sealed class EstadoDoThrottle
{
    public bool UsarFallbackSql { get; set; }
}

/// <summary>
/// 07 E3-D2 e E3-D10. Ordem: teto global → dispositivo (<c>SET NX</c> 5 min) → usuário logado (<c>SET NX</c>) → IP (<c>INCR</c>, acima de
/// <c>Pedidos:LimitePorIp</c>). O IP só é contado se o dispositivo e o usuário passaram: um pedido barrado não gasta a cota do IP dos outros.
/// Uma reserva feita antes de um 429 por IP não é desfeita (desfazer exigiria script Lua; o efeito é o dispositivo esperar 5 min, o
/// que o anti-spam já impõe). Com mais de uma regra falhando, o <c>Retry-After</c> é o MAIOR TTL. As chaves levam só HMAC (o prefixo
/// <c>wr:</c> é do <c>RedisRequestThrottle</c>). Redis fora: o disjuntor abre e o handler aplica o fallback SQL.
/// </summary>
public sealed class ThrottleDePedido(
    IRequestThrottle contadores,
    IConsultasDePedidos consultas,
    IListenerIdentity identidade,
    DisjuntorDoRedis disjuntor,
    CacheDoTeto cacheDoTeto,
    EstadoDoThrottle estado,
    IOptions<PedidosOptions> opcoes,
    ILogger<ThrottleDePedido> logger) : IThrottleDoRequest<CriarPedidoCommand>
{
    public static readonly TimeSpan Janela = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan EsperaDoTeto = TimeSpan.FromSeconds(60);

    public async Task AvaliarAsync(CriarPedidoCommand request, CancellationToken ct)
    {
        var o = opcoes.Value;
        if (await cacheDoTeto.AtingidoAsync(() => consultas.TetoDePendentesAtingidoAsync(o.TetoPendentes, ct)))
            throw new MuitosPedidosException(EsperaDoTeto);

        var permissao = disjuntor.Pedir();
        if (permissao == PermissaoDoRedis.Evitar)
        {
            estado.UsarFallbackSql = true;
            return;
        }

        TimeSpan? espera;
        try
        {
            espera = await NoRedisAsync(request, o.LimitePorIp, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (disjuntor.Falha(permissao))
                logger.LogWarning("PEDIDO_THROTTLE_REDIS_INDISPONIVEL ({Tipo}): anti-spam no SQL por {Segundos}s (modo degradado)",
                    ex.GetType().Name, DisjuntorDoRedis.TempoAberto.TotalSeconds);
            estado.UsarFallbackSql = true;
            return;
        }

        disjuntor.Sucesso(permissao);
        if (espera is { } tempo) throw new MuitosPedidosException(tempo);
    }

    /// <summary>O maior TTL das regras que barraram, ou <c>null</c> se o pedido passa.</summary>
    private async Task<TimeSpan?> NoRedisAsync(CriarPedidoCommand request, int limitePorIp, CancellationToken ct)
    {
        var dispositivo = identidade.DeviceHash(request.ListenerId);
        TimeSpan? espera = null;

        if (!await contadores.TentarReservarAsync(ChaveDoDispositivo(dispositivo), Janela, ct))
            espera = Maior(espera, await contadores.TempoRestanteAsync(ChaveDoDispositivo(dispositivo), ct));

        if (request.UsuarioId is { } usuario && !await contadores.TentarReservarAsync(ChaveDoUsuario(usuario), Janela, ct))
            espera = Maior(espera, await contadores.TempoRestanteAsync(ChaveDoUsuario(usuario), ct));

        if (espera is not null) return espera;

        var chaveIp = ChaveDoIp(identidade.IpHash(request.Ip));
        if (await contadores.IncrementarAsync(chaveIp, Janela, ct) > limitePorIp)
            espera = Maior(espera, await contadores.TempoRestanteAsync(chaveIp, ct));
        return espera;
    }

    // TTL ausente (a chave expirou entre o SET e a leitura) ou zero: no mínimo 1 s, para o Retry-After nunca ser 0.
    private static TimeSpan Maior(TimeSpan? atual, TimeSpan? ttl)
    {
        var novo = ttl is { } t && t > TimeSpan.Zero ? t : TimeSpan.FromSeconds(1);
        return atual is { } a && a > novo ? a : novo;
    }

    internal static string ChaveDoDispositivo(byte[] hash) => $"throttle:pedido:dev:{Convert.ToHexString(hash)}";

    internal static string ChaveDoIp(byte[] hash) => $"throttle:pedido:ip:{Convert.ToHexString(hash)}";

    internal static string ChaveDoUsuario(Guid id) => $"throttle:pedido:usr:{id:N}";
}
