using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Persistencia.Outbox;

public sealed record ResultadoDoLote(int Lidos, int Falhas);

/// <summary>
/// Um passe do OutboxDispatcher (00 §fluxos, D22): pega até 50 eventos pendentes com <c>UPDLOCK, READPAST</c> (duas
/// réplicas nunca pegam o mesmo evento; a que chega depois pula as linhas travadas), entrega e marca
/// <c>ProcessadoEmUtc</c> na mesma transação. Entrega PELO MENOS uma vez: se o processo cair entre o envio e o commit, o
/// evento sai de novo com o mesmo <c>eventoId</c>, e o cliente descarta o repetido.
/// </summary>
public sealed class OutboxProcessor(RadioDbContext db, IRealtimeNotifier notifier, IClock clock, ILogger<OutboxProcessor> logger)
{
    public const int TamanhoDoLote = 50;

    public async Task<ResultadoDoLote> ProcessarLoteAsync(CancellationToken ct)
    {
        // Só os testes HTTP usam o provedor em memória (sem transação nem dica de trava); lá há um único consumidor.
        if (!db.Database.IsSqlServer())
        {
            return await EntregarAsync(await db.EventosOutbox
                .Where(e => e.ProcessadoEmUtc == null && e.Tentativas < EventoOutbox.MaximoDeTentativas)
                .OrderBy(e => e.Id).Take(TamanhoDoLote).ToListAsync(ct), ct);
        }

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async ct2 =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct2);
            // As travas de linha valem até o commit. ROWLOCK evita que a trava suba para a página e esconda vizinhos do
            // READPAST da outra réplica. O filtro é o do índice IX_EventoOutbox_Pendentes.
            var lote = await db.EventosOutbox
                .FromSqlInterpolated($"SELECT TOP ({TamanhoDoLote}) * FROM infra.EventoOutbox WITH (UPDLOCK, READPAST, ROWLOCK) WHERE ProcessadoEmUtc IS NULL AND Tentativas < {EventoOutbox.MaximoDeTentativas} ORDER BY Id")
                .ToListAsync(ct2);
            var resultado = await EntregarAsync(lote, ct2);
            await tx.CommitAsync(ct2);
            return resultado;
        }, ct);
    }

    private async Task<ResultadoDoLote> EntregarAsync(List<EventoOutbox> lote, CancellationToken ct)
    {
        var falhas = 0;
        foreach (var evento in lote)
        {
            try
            {
                await EntregarAsync(evento, ct);
                evento.MarcarProcessado(clock.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Um evento ruim não segura os outros do lote; depois de 10 tentativas ele fica parado para análise.
                falhas++;
                evento.RegistrarFalha($"{ex.GetType().Name}: {ex.Message}");
                logger.LogWarning(ex, "OUTBOX_FALHA: evento {EventoId} ({Tipo}), tentativa {Tentativa} de {Maximo}",
                    evento.Id, evento.Tipo, evento.Tentativas, EventoOutbox.MaximoDeTentativas);
            }
        }

        if (lote.Count > 0) await db.SaveChangesAsync(ct);
        return new ResultadoDoLote(lote.Count, falhas);
    }

    private Task EntregarAsync(EventoOutbox evento, CancellationToken ct)
    {
        using var documento = JsonDocument.Parse(evento.Payload);
        var envelope = documento.RootElement;
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("dados", out var dados) || dados.ValueKind != JsonValueKind.Object)
            throw new JsonException("Envelope sem um objeto em 'dados'.");

        // Evento interno: não vai para cliente nenhum, derruba as conexões do usuário (S-M08).
        if (evento.Tipo == EventosDoOutbox.UsuarioSessoesRevogadas)
        {
            if (!dados.TryGetProperty("usuarioId", out var id) || !id.TryGetGuid(out var usuarioId))
                throw new JsonException("UsuarioSessoesRevogadas sem 'usuarioId' válido.");
            return notifier.DesconectarUsuarioAsync(usuarioId, ct);
        }

        var grupos = Destino(envelope);

        // 02 §10: todo evento sai com eventoId e ocorridoEmUtc; o id do outbox é estável entre reenvios.
        var carga = JsonObject.Create(dados.Clone())!;
        carga["eventoId"] = evento.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        carga["ocorridoEmUtc"] = DateTime.SpecifyKind(evento.CriadoEmUtc, DateTimeKind.Utc);

        return grupos is null
            ? notifier.NotificarTodosAsync(evento.Tipo, carga, ct)
            : notifier.NotificarGruposAsync(grupos, evento.Tipo, carga, ct);
    }

    /// <summary>
    /// Destino explícito (00 §fluxos): <c>"grupos": null</c> = todos (devolve null); lista preenchida = esses grupos.
    /// Propriedade ausente, lista vazia ou nome em branco é ERRO: na dúvida o evento falha, nunca vira broadcast.
    /// </summary>
    private static string[]? Destino(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("grupos", out var grupos))
            throw new JsonException("Envelope sem 'grupos' (use null para todos os clientes).");
        if (grupos.ValueKind == JsonValueKind.Null) return null;
        if (grupos.ValueKind != JsonValueKind.Array || grupos.GetArrayLength() == 0)
            throw new JsonException("'grupos' precisa ser null (todos) ou uma lista com ao menos um grupo.");

        var nomes = grupos.EnumerateArray().Select(g => g.ValueKind == JsonValueKind.String ? g.GetString() : null).ToArray();
        if (nomes.Any(string.IsNullOrWhiteSpace)) throw new JsonException("'grupos' tem um nome de grupo inválido.");
        return nomes!;
    }
}
