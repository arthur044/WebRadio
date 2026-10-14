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

    public void EnfileirarParaTodos(string tipo, object carga) => Adicionar(tipo, carga, grupos: null);

    public void EnfileirarParaGrupos(IReadOnlyCollection<string> grupos, string tipo, object carga)
    {
        ArgumentNullException.ThrowIfNull(grupos);
        if (grupos.Count == 0 || grupos.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Informe ao menos um grupo, sem nome em branco. Para todos os clientes, use EnfileirarParaTodos.", nameof(grupos));
        Adicionar(tipo, carga, [.. grupos]);
    }

    public void EnfileirarSessoesRevogadas(Guid usuarioId)
    {
        if (usuarioId == Guid.Empty) throw new ArgumentException("Usuário inválido.", nameof(usuarioId));
        Gravar(EventosDoOutbox.UsuarioSessoesRevogadas, new { usuarioId }, grupos: null);
    }

    private void Adicionar(string tipo, object carga, string[]? grupos)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tipo);
        ArgumentNullException.ThrowIfNull(carga);
        // O tipo interno nunca sai por aqui: com destino de cliente ele seria tratado como ordem de derrubar conexões.
        if (tipo == EventosDoOutbox.UsuarioSessoesRevogadas)
            throw new ArgumentException($"{tipo} é interno: use EnfileirarSessoesRevogadas.", nameof(tipo));
        Gravar(tipo, carga, grupos);
    }

    // O destino viaja junto com os dados: o OutboxProcessor não precisa conhecer cada tipo de evento.
    // "grupos": null = todos; lista preenchida = só esses grupos. Lista vazia nunca é gravada (e, se aparecer, é erro).
    private void Gravar(string tipo, object carga, string[]? grupos) =>
        db.EventosOutbox.Add(new EventoOutbox(tipo, JsonSerializer.Serialize(new { grupos, dados = carga }, Json)));
}
