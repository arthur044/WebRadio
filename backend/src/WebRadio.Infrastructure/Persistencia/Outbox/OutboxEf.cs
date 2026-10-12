using System.Text.Json;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Persistencia.Outbox;

/// <summary>
/// Só adiciona a linha ao DbContext do escopo: quem grava é o <c>SaveChanges</c> do handler, na mesma transação da mudança
/// de estado (D22). Sem SaveChanges, o evento não existe.
/// </summary>
public sealed class OutboxEf(RadioDbContext db) : IOutbox
{
    /// <summary>O mesmo formato da API (camelCase): o payload vai como está para os clientes do hub.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Enfileirar(string tipo, object carga)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tipo);
        db.EventosOutbox.Add(new EventoOutbox(tipo, JsonSerializer.Serialize(carga, Json)));
    }
}
