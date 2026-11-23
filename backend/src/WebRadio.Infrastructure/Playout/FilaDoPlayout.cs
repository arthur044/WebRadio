using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Playout;

internal sealed class LinhaDoPedido
{
    public Guid PedidoId { get; set; }

    public Guid MidiaId { get; set; }
}

internal sealed class LinhaDaCandidata
{
    public Guid Id { get; set; }

    public string? Titulo { get; set; }

    public string? Artista { get; set; }
}

/// <summary>
/// O SQL do Atlas (<c>docs/pareceres/e4-a01a-consultas-playout.md</c>, seções 2.1 e 2.2). O texto e os hints <c>INDEX(...)</c> são o plano medido: um índice renomeado falha em tempo de execução e o
/// teste com SQL real acusa. Só roda no SQL Server (<c>UPDLOCK, READPAST</c> e a coluna calculada <c>Sorteio</c> não existem no provedor em memória).
/// </summary>
public sealed class FilaDoPlayout(RadioDbContext db) : IFilaDoPlayout
{
    // E4-D5, captura e marcação numa só instrução (a tabela variável evita o OUTPUT direto, que o EF não consegue embrulhar numa subconsulta, como no CapturaDeMidia).
    // O EXISTS da mídia tira o skip do READPAST (parecer do Atlas, seção 3): uma segunda chamada simultânea pode receber 204 e cair na rotação; a invariante (nunca o mesmo pedido) vale.
    private const string SqlPedido = $"""
        SET XACT_ABORT ON;
        DECLARE @capturado TABLE (PedidoId uniqueidentifier, MidiaId uniqueidentifier);

        ;WITH c AS (
            SELECT TOP (1) p.Id, p.MidiaId, p.EnviadoPlayoutEmUtc, p.EnviadoPlayoutInstancia
            FROM interacao.PedidoMusica AS p WITH (UPDLOCK, READPAST, ROWLOCK, INDEX(IX_PedidoMusica_Playout))
            WHERE p.Status = 2 AND p.MidiaId IS NOT NULL AND p.ModeradoEmUtc >= @corte24h
              AND (p.EnviadoPlayoutEmUtc IS NULL OR p.EnviadoPlayoutInstancia IS NULL OR p.EnviadoPlayoutInstancia <> @instancia)
              AND EXISTS (SELECT 1 FROM midia.ArquivoMidia AS m WHERE m.Id = p.MidiaId AND m.StatusSanitizacao = 4 AND m.Ativo = 1)
            ORDER BY p.ModeradoEmUtc)
        UPDATE c SET EnviadoPlayoutEmUtc = @agora, EnviadoPlayoutInstancia = @instancia
        OUTPUT inserted.Id, inserted.MidiaId INTO @capturado;

        SELECT PedidoId, MidiaId FROM @capturado;
        """;

    // E4-A01b: TOP (@k) com K = 5 (texto medido pelo Atlas, infra/perf/medir-e4-a01b-k.sql). O hint INDEX(...) e o StatusSanitizacao = 4 AND Ativo = 1 LITERAL fazem parte do contrato: o
    // nested loops com esse índice é o plano certo, e parametrizar o status ou tirar o hint salta de ~13 para ~900 leituras.
    private const string SqlForaDaJanelaDepois = """
        SELECT TOP (@k) a.Id, a.Titulo, a.Artista
        FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
        WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio >= @r AND a.Id <> @emVoo
          AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo))
                          WHERE r.IniciadoEmUtc >= @corteJanela AND r.MidiaId = a.Id)
        ORDER BY a.Sorteio;
        """;

    private const string SqlForaDaJanelaAntes = """
        SELECT TOP (@k) a.Id, a.Titulo, a.Artista
        FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
        WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio < @r AND a.Id <> @emVoo
          AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo))
                          WHERE r.IniciadoEmUtc >= @corteJanela AND r.MidiaId = a.Id)
        ORDER BY a.Sorteio;
        """;

    private const string SqlMaisAntigaDaJanela = """
        SELECT TOP (1) a.Id, a.Titulo, a.Artista
        FROM (SELECT r.MidiaId, MAX(r.IniciadoEmUtc) AS Ultima
              FROM midia.Reproducao AS r WHERE r.IniciadoEmUtc >= @corteJanela GROUP BY r.MidiaId) AS u
        JOIN midia.ArquivoMidia AS a ON a.Id = u.MidiaId
        WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Id <> @emVoo
        ORDER BY u.Ultima, a.Id;
        """;

    public async Task<PedidoParaTocar?> CapturarPedidoAsync(Guid instancia, DateTime agora, CancellationToken ct)
    {
        var linhas = await db.Database.SqlQueryRaw<LinhaDoPedido>(SqlPedido,
            new SqlParameter("@corte24h", System.Data.SqlDbType.DateTime2) { Value = agora.AddHours(-24), Scale = 3 },
            new SqlParameter("@agora", System.Data.SqlDbType.DateTime2) { Value = agora, Scale = 3 },
            new SqlParameter("@instancia", System.Data.SqlDbType.UniqueIdentifier) { Value = instancia }).ToListAsync(ct);
        return linhas.Count == 0 ? null : new PedidoParaTocar(linhas[0].PedidoId, linhas[0].MidiaId);
    }

    public async Task<IReadOnlyList<CandidataDaRotacao>> CandidatasForaDaJanelaAsync(TipoMidia tipo, int sorteio, DateTime corteDaJanela, Guid emVoo, CancellationToken ct)
    {
        // Busca 1: a partir da chave. Busca 2 SÓ se vieram menos de K: completa com as do início do círculo (Sorteio < @r), que nunca repetem as da busca 1.
        var candidatas = await Buscar(SqlForaDaJanelaDepois, tipo, sorteio, corteDaJanela, emVoo, IFilaDoPlayout.CandidatasPorSorteio, ct);
        if (candidatas.Count < IFilaDoPlayout.CandidatasPorSorteio)
            candidatas.AddRange(await Buscar(SqlForaDaJanelaAntes, tipo, sorteio, corteDaJanela, emVoo, IFilaDoPlayout.CandidatasPorSorteio - candidatas.Count, ct));
        return candidatas;
    }

    public async Task<CandidataDaRotacao?> MaisAntigaDaJanelaAsync(TipoMidia tipo, DateTime corteDaJanela, Guid emVoo, CancellationToken ct) =>
        (await Buscar(SqlMaisAntigaDaJanela, tipo, 0, corteDaJanela, emVoo, 1, ct)).FirstOrDefault();

    private async Task<List<CandidataDaRotacao>> Buscar(string sql, TipoMidia tipo, int r, DateTime corteDaJanela, Guid emVoo, int k, CancellationToken ct)
    {
        var linhas = await db.Database.SqlQueryRaw<LinhaDaCandidata>(sql,
            new SqlParameter("@tipo", System.Data.SqlDbType.TinyInt) { Value = (byte)tipo },
            new SqlParameter("@r", System.Data.SqlDbType.Int) { Value = r },
            new SqlParameter("@corteJanela", System.Data.SqlDbType.DateTime2) { Value = corteDaJanela, Scale = 0 },
            new SqlParameter("@emVoo", System.Data.SqlDbType.UniqueIdentifier) { Value = emVoo },
            new SqlParameter("@k", System.Data.SqlDbType.Int) { Value = k }).ToListAsync(ct);
        return [.. linhas.Select(l => new CandidataDaRotacao(l.Id, l.Titulo, l.Artista))];
    }
}

/// <summary>A faixa em voo (E4-D4): Redis com a memória desta instância como reserva, como o "tocando agora". Redis fora não é erro para o Liquidsoap.</summary>
public sealed class FaixaEmVoo(ILogger<FaixaEmVoo> logger, IConnectionMultiplexer? redis = null) : IFaixaEmVoo
{
    private static readonly RedisKey Chave = "wr:playout:em-voo";

    private static readonly TimeSpan Validade = TimeSpan.FromHours(1);

    private Guid _local = Guid.Empty;

    public async Task DefinirAsync(Guid midiaId, CancellationToken ct)
    {
        _local = midiaId;
        if (redis is null) return;
        try
        {
            await redis.GetDatabase().StringSetAsync(Chave, midiaId.ToString("D"), Validade);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning("REDIS_INDISPONIVEL: a faixa em voo ficou só na memória desta instância ({Tipo})", ex.GetType().Name);
        }
    }

    public async Task<Guid> ObterAsync(CancellationToken ct)
    {
        if (redis is null) return _local;
        try
        {
            var valor = await redis.GetDatabase().StringGetAsync(Chave);
            return !valor.IsNullOrEmpty && Guid.TryParse(valor.ToString(), out var id) ? id : _local;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return _local;
        }
    }
}

/// <summary>Sorteio de verdade; o teste injeta um determinístico.</summary>
public sealed class AleatorioDoSistema : IAleatorio
{
    public int Proximo(int minimo, int maximoExclusivo) => Random.Shared.Next(minimo, maximoExclusivo);

    public int ChaveDoSorteio() => Random.Shared.Next(int.MinValue, int.MaxValue);
}
