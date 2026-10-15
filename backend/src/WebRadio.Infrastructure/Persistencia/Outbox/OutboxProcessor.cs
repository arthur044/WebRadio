using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Persistencia.Outbox;

/// <param name="Lidos">Eventos pegos neste passe.</param>
/// <param name="Falhas">Falhas que ainda serão tentadas de novo (destino fora, tempo esgotado): fazem o dispatcher esperar mais.</param>
/// <param name="Parados">Eventos que saíram da fila neste passe sem entrega (payload inválido ou 10 tentativas).</param>
public sealed record ResultadoDoLote(int Lidos, int Falhas, int Parados = 0);

/// <summary>
/// Um passe do OutboxDispatcher (00 §fluxos, D22): pega até 50 eventos pendentes com <c>UPDLOCK, READPAST</c> (duas
/// réplicas nunca pegam o mesmo evento; a que chega depois pula as linhas travadas), entrega e marca
/// <c>ProcessadoEmUtc</c> na mesma transação. Entrega PELO MENOS uma vez: se o processo cair entre o envio e o commit, o
/// evento sai de novo com o mesmo <c>eventoId</c>, e o cliente descarta o repetido.
/// </summary>
public sealed class OutboxProcessor(RadioDbContext db, IRealtimeNotifier notifier, IClock clock, ILogger<OutboxProcessor> logger)
{
    public const int TamanhoDoLote = 50;

    /// <summary>
    /// Teto de cada entrega (SignalR/Redis). Ela roda DENTRO da transação, com as linhas do lote travadas e uma conexão do
    /// pool ocupada: uma chamada pendurada não pode segurar isso sem limite. Estourou, o evento conta uma falha e o lote segue.
    /// </summary>
    public TimeSpan TempoMaximoPorEvento { get; init; } = TimeSpan.FromSeconds(5);

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
        var (falhas, parados) = (0, 0);
        foreach (var evento in lote)
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TempoMaximoPorEvento);
            try
            {
                // WaitAsync garante o teto mesmo que o destino ignore o token (o publish do Redis não recebe um).
                await EntregarAsync(evento, limite.Token).WaitAsync(limite.Token);
                evento.MarcarProcessado(clock.UtcNow);
            }
            catch (JsonException ex)
            {
                // Payload fora do envelope não melhora com o tempo: para na hora, sem atrasar os eventos saudáveis com o
                // backoff do dispatcher.
                evento.Parar($"{nameof(JsonException)}: {ex.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Um evento ruim não segura os outros do lote; depois de 10 tentativas ele fica parado para análise.
                // Cancelamento com o `ct` de fora intacto é o teto por evento, não o desligamento do host.
                falhas++;
                evento.RegistrarFalha(ex is OperationCanceledException
                    ? $"Tempo esgotado: a entrega passou de {TempoMaximoPorEvento.TotalSeconds:0.###} s."
                    : $"{ex.GetType().Name}: {ex.Message}");
                logger.LogWarning(ex, "OUTBOX_FALHA: evento {EventoId} ({Tipo}), tentativa {Tentativa} de {Maximo}",
                    evento.Id, evento.Tipo, evento.Tentativas, EventoOutbox.MaximoDeTentativas);
            }

            if (!evento.Parado) continue;

            // Ninguém vai tentar de novo. Para UsuarioSessoesRevogadas isso quer dizer que as conexões do hub daquele
            // usuário só caem no exp do token: é um alerta para o operador, não um aviso.
            parados++;
            logger.LogError("OUTBOX_ESGOTADO: evento {EventoId} ({Tipo}) saiu da fila sem entrega: {Erro}", evento.Id, evento.Tipo, evento.UltimoErro);
        }

        if (lote.Count > 0) await db.SaveChangesAsync(ct);
        return new ResultadoDoLote(lote.Count, falhas, parados);
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
        carga["ocorridoEmUtc"] = evento.CriadoEmUtc; // Kind = Utc: o RadioDbContext lê toda data como UTC

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
