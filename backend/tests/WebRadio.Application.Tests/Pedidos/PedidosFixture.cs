using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Pedidos;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Interacao;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using WebRadio.Infrastructure.Seguranca;
using WebRadio.Application.Tests.Auth;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Pedidos;

public sealed class ConsultasDePedidosFalsas : IConsultasDePedidos
{
    public Guid? Programa { get; set; }

    public bool TetoAtingido { get; set; }

    public int ConsultasDoTeto { get; private set; }

    public EsperaDoThrottle Espera { get; set; } = EsperaDoThrottle.Livre;

    public int ConsultasDoFallback { get; private set; }

    public Task<Guid?> ProgramaDaJanelaAsync(CancellationToken ct) => Task.FromResult(Programa);

    public Task<bool> TetoDePendentesAtingidoAsync(int teto, CancellationToken ct)
    {
        ConsultasDoTeto++;
        return Task.FromResult(TetoAtingido);
    }

    public Task<EsperaDoThrottle> EsperaDoFallbackAsync(byte[] deviceHash, byte[] ipHash, Guid? usuarioId, int limitePorIp, CancellationToken ct)
    {
        ConsultasDoFallback++;
        return Task.FromResult(Espera);
    }
}

/// <summary>O Redis de mentira: o throttle em memória de verdade, que para de responder quando <see cref="Fora"/> é verdadeiro.</summary>
public sealed class RedisQueCai(IClock clock) : IRequestThrottle
{
    private readonly MemoryRequestThrottle _real = new(clock);

    public bool Fora { get; set; }

    /// <summary>A chamada é cancelada (o cliente fechou a conexão) em vez de falhar.</summary>
    public bool Cancelar { get; set; }

    public int Chamadas { get; private set; }

    private T Seguir<T>(Func<T> operacao)
    {
        Chamadas++;
        if (Cancelar) throw new OperationCanceledException();
        return Fora ? throw new InvalidOperationException("Redis fora") : operacao();
    }

    public Task<bool> TentarReservarAsync(string chave, TimeSpan ttl, CancellationToken ct = default) => Seguir(() => _real.TentarReservarAsync(chave, ttl, ct));

    public Task<long> IncrementarAsync(string chave, TimeSpan ttl, CancellationToken ct = default) => Seguir(() => _real.IncrementarAsync(chave, ttl, ct));

    public Task<long> LerAsync(string chave, CancellationToken ct = default) => Seguir(() => _real.LerAsync(chave, ct));

    public Task RemoverAsync(string chave, CancellationToken ct = default) => Seguir(() => _real.RemoverAsync(chave, ct));

    public Task<TimeSpan?> TempoRestanteAsync(string chave, CancellationToken ct = default) => Seguir(() => _real.TempoRestanteAsync(chave, ct));
}

public sealed class TransacaoSemTrava : ITransacaoComTrava
{
    public bool Ocupada { get; set; }

    public List<string> Recursos { get; } = [];

    public IReadOnlyList<string> ConjuntosDeTravas { get; private set; } = [];

    public Task<T> ExecutarAsync<T>(string recurso, Func<CancellationToken, Task<T>> acao, CancellationToken ct)
    {
        Recursos.Add(recurso);
        return acao(ct);
    }

    public Task<T> ExecutarAsync<T>(string recurso, TimeSpan espera, Func<CancellationToken, Task<T>> acao, CancellationToken ct)
    {
        Recursos.Add(recurso);
        return Ocupada ? throw new WebRadio.Domain.Erros.TravaOcupadaException() : acao(ct);
    }

    public Task<T> ExecutarAsync<T>(IReadOnlyList<string> recursos, TimeSpan espera, Func<CancellationToken, Task<T>> acao, CancellationToken ct)
    {
        Recursos.AddRange(recursos);
        ConjuntosDeTravas = recursos;
        return Ocupada ? throw new WebRadio.Domain.Erros.TravaOcupadaException() : acao(ct);
    }
}

/// <summary>Banco em memória + relógio falso + os handlers de pedido montados como na Api (E3-F01 a F03).</summary>
public sealed class PedidosFixture
{
    public static readonly byte[] Pepper = Enumerable.Range(1, 48).Select(i => (byte)i).ToArray();

    public PedidosFixture(PedidosOptions? opcoes = null)
    {
        Db = NovoDb();
        Relogio = new RelogioFalso(Agora);
        Opcoes = opcoes ?? new PedidosOptions();
        Redis = new RedisQueCai(Relogio);
        Hasher = new IpHasher(Pepper);
        Identidade = new ListenerIdentity(Pepper, Hasher);
        Disjuntor = new DisjuntorDoRedis(Relogio);
        Cache = new CacheDoTeto(Relogio);
        Estado = new EstadoDoThrottle();
        Outbox = new OutboxEf(Db);
    }

    public RadioDbContext Db { get; }

    public Guid UsuarioDaChaveDeTeste { get; } = Guid.NewGuid();

    public RelogioFalso Relogio { get; }

    public PedidosOptions Opcoes { get; }

    public RedisQueCai Redis { get; }

    public ConsultasDePedidosFalsas Consultas { get; } = new();

    public TransacaoSemTrava Transacao { get; } = new();

    public ListenerIdentity Identidade { get; }

    public IpHasher Hasher { get; }

    public DisjuntorDoRedis Disjuntor { get; }

    public CacheDoTeto Cache { get; }

    public EstadoDoThrottle Estado { get; }

    public OutboxEf Outbox { get; }

    public ThrottleDePedido Throttle => new(Redis, Consultas, Identidade, Hasher, Disjuntor, Cache, Estado, Options.Create(Opcoes), NullLogger<ThrottleDePedido>.Instance);

    public CriarPedidoHandler Criar => new(Db, Transacao, Consultas, Identidade, Hasher, Outbox, Relogio, Estado, Options.Create(Opcoes));

    public AprovarPedidoHandler Aprovar => new(Db, Outbox, Relogio);

    public RejeitarPedidoHandler Rejeitar => new(Db, Outbox, Relogio);

    public MarcarPedidoTocadoHandler Tocar => new(Db, Outbox, Relogio);

    public MeusPedidosHandler Meus => new(Db, Identidade);

    public ListarPedidosHandler Listar => new(Db, Relogio);

    public static CriarPedidoCommand Comando(Guid? listenerId = null, string ip = "203.0.113.7", Guid? usuarioId = null, string nome = "Maria",
        string titulo = "Garota de Ipanema", string artista = "Tom Jobim", string? mensagem = "Para a minha mãe") =>
        new(listenerId ?? Guid.NewGuid(), IPAddress.Parse(ip), usuarioId, nome, titulo, artista, mensagem);

    /// <summary>Insere um pedido direto no banco (sem throttle), no estado pedido.</summary>
    public async Task<PedidoMusica> InserirAsync(Guid? listenerId = null, int idadeEmMinutos = 0, StatusPedido status = StatusPedido.Pendente,
        Guid? moderadorId = null, bool semHash = false, string nome = "Maria", int moderadoHaMinutos = 0)
    {
        var criado = Agora.AddMinutes(-idadeEmMinutos);
        var hash = semHash ? null : Identidade.DeviceHash(listenerId ?? Guid.NewGuid());
        var p = new PedidoMusica(Guid.NewGuid(), null, null, nome, "Música", "Artista", null, hash, null, criado);
        if (status != StatusPedido.Pendente)
        {
            p.Aprovar(moderadorId ?? Guid.NewGuid(), null, Agora.AddMinutes(-moderadoHaMinutos));
            if (status == StatusPedido.Tocado) p.MarcarTocado(Agora.AddMinutes(-moderadoHaMinutos));
        }

        Db.PedidosMusica.Add(p);
        await Db.SaveChangesAsync();
        return p;
    }

    public List<(string Tipo, JsonElement Dados, string[]? Grupos)> Eventos()
    {
        var saida = new List<(string, JsonElement, string[]?)>();
        foreach (var e in Db.EventosOutbox.OrderBy(x => x.Id).ToList())
        {
            using var doc = JsonDocument.Parse(e.Payload);
            var grupos = doc.RootElement.GetProperty("grupos");
            saida.Add((e.Tipo, doc.RootElement.GetProperty("dados").Clone(),
                grupos.ValueKind == JsonValueKind.Null ? null : [.. grupos.EnumerateArray().Select(g => g.GetString()!)]));
        }

        return saida;
    }
}
