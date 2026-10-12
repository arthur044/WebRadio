using System.Text.Json;
using System.Text.Json.Serialization;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Persistencia.Outbox;

/// <summary>
/// Só adiciona a linha ao DbContext do escopo: quem grava é o <c>SaveChanges</c> do handler, na mesma transação da mudança
/// de estado (D22). Sem SaveChanges, o evento não existe.
/// </summary>
public sealed class OutboxEf(RadioDbContext db) : IOutbox
{
    /// <summary>O mesmo formato da API (camelCase, enum como string): o payload vai como está para os clientes do hub.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public void Enfileirar(string tipo, object carga, params string[] grupos)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tipo);
        ArgumentNullException.ThrowIfNull(carga);
        // O destino viaja junto com os dados: o OutboxProcessor não precisa conhecer cada tipo de evento.
        db.EventosOutbox.Add(new EventoOutbox(tipo, JsonSerializer.Serialize(new { grupos, dados = carga }, Json)));
    }
}
