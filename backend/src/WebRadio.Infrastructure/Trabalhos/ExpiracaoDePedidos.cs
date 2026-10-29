using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebRadio.Application.Features.Pedidos;
using WebRadio.Domain.Enums;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Trabalhos;

public interface IExpiracaoDePedidos
{
    /// <summary>Devolve quantos pedidos expirou neste ciclo (todos os lotes).</summary>
    Task<int> ExecutarAsync(CancellationToken ct);
}

/// <summary>
/// 07 E3-F04, Fluxo B passo 8: <c>Pendente → Expirado</c> quando o pedido tem mais de <c>Pedidos:ExpiracaoMinutos</c> (padrão 120), com o
/// relógio do SQL (<c>SYSUTCDATETIME()</c>), em LOTES de no máximo 1.000 (abaixo do limiar de ~5.000 locks que escala o lock para a tabela;
/// no acúmulo de 25.000 vencidos com o Worker parado, cada lote é uma transação curta). O <c>OUTPUT</c> do próprio <c>UPDATE</c> alimenta o
/// <c>PedidoModerado { status: Expirado }</c> no outbox na MESMA transação de cada lote (E3-D3), para <c>locutores</c> e <c>listener:{hash}</c>
/// (o hash vem do <c>OUTPUT</c>; pedido sem hash só vai para <c>locutores</c>). Repete até o lote vir menor que <c>@lote</c>.
/// <para>
/// Idempotente por construção: o <c>UPDATE</c> só pega <c>Status = 1</c>, então rodar de novo (ou dois Workers ao mesmo tempo: o segundo
/// espera a trava de linha e reavalia o filtro) não gera evento repetido. O filtro <c>Status = 1</c> é CONSTANTE, para o
/// <c>IX_PedidoMusica_Fila</c> (filtrado, chave <c>CriadoEmUtc</c>) ser usado e o lote ser um seek de faixa.
/// </para>
/// </summary>
public sealed class ExpiracaoDePedidos(RadioDbContext db, IOptions<PedidosOptions> opcoes) : IExpiracaoDePedidos
{
    public const int LotePadrao = 1000;

    public int Lote { get; init; } = LotePadrao;

    // O número vem do enum, não de literal: se o enum mudar, o SQL acompanha (e o teste que fixa os valores avisa que o índice filtrado precisa acompanhar).
    private static readonly byte Pendente = (byte)StatusPedido.Pendente;
    private static readonly byte Expirado = (byte)StatusPedido.Expirado;

    private static readonly string Sql = $"""
        SET XACT_ABORT ON;
        BEGIN TRAN;
        DECLARE @expirados TABLE (Id uniqueidentifier, Hash binary(32) NULL);
        DECLARE @n int;

        -- O hint é do Atlas (E3-A01): sem ele o otimizador varre o clustered inteiro (19.845 leituras por ciclo vazio, 26.880 por lote com
        -- 25.000 vencidos); com ele são 3 e ~12.400 (os 1.000 key lookups mais a manutenção do índice). Se o índice for renomeado, o teste
        -- com vários lotes falha.
        UPDATE TOP (@lote) p SET Status = {Expirado}
        OUTPUT inserted.Id, inserted.ListenerDeviceHash INTO @expirados
        FROM interacao.PedidoMusica AS p WITH (INDEX(IX_PedidoMusica_Fila))
        WHERE p.Status = {Pendente} AND p.CriadoEmUtc < DATEADD(MINUTE, -@expiracao, SYSUTCDATETIME());

        INSERT infra.EventoOutbox (Tipo, Payload)
        SELECT 'PedidoModerado',
               JSON_OBJECT('grupos': CASE WHEN Hash IS NULL THEN JSON_ARRAY('locutores')
                                          ELSE JSON_ARRAY('locutores', 'listener:' + CONVERT(varchar(64), Hash, 2)) END,
                           'dados': JSON_OBJECT('pedidoId': LOWER(CONVERT(varchar(36), Id)), 'status': '{nameof(StatusPedido.Expirado)}'))
        FROM @expirados;

        SELECT @n = COUNT(*) FROM @expirados;
        COMMIT;
        -- Depois do COMMIT: se ele falhar, o laço não conta lote que não existiu.
        SELECT @n AS [Value];
        """;

    public async Task<int> ExecutarAsync(CancellationToken ct)
    {
        var expiracao = opcoes.Value.ExpiracaoMinutos;
        var total = 0;
        int n;
        do
        {
            ct.ThrowIfCancellationRequested();
            n = (await db.Database.SqlQueryRaw<int>(Sql,
                new Microsoft.Data.SqlClient.SqlParameter("@lote", Lote),
                new Microsoft.Data.SqlClient.SqlParameter("@expiracao", expiracao)).ToListAsync(ct)).Single();
            total += n;
        } while (n == Lote);
        return total;
    }
}
