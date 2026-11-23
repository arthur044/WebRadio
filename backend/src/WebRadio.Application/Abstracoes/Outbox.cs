namespace WebRadio.Application.Abstracoes;

/// <summary>
/// Outbox transacional (D22): o evento entra no mesmo <c>SaveChanges</c> da mudança de estado e o OutboxDispatcher o
/// entrega depois. Handlers nunca chamam o SignalR direto.
/// </summary>
/// <remarks>
/// O destino é sempre EXPLÍCITO (00 §fluxos, envelope <c>{ grupos, dados }</c>): não existe "sem grupo = todos". Esquecer o
/// grupo de um evento com dado pessoal (ex.: <c>PedidoCriado</c>) não pode virar broadcast.
/// </remarks>
public interface IOutbox
{
    /// <summary>Evento público: vai para todos os clientes do hub (<c>"grupos": null</c> no envelope).</summary>
    void EnfileirarParaTodos(string tipo, object carga);

    /// <summary>Só para os grupos informados (02 §10). Lista vazia ou com nome em branco lança.</summary>
    void EnfileirarParaGrupos(IReadOnlyCollection<string> grupos, string tipo, object carga);

    /// <summary>
    /// Evento interno <see cref="EventosDoOutbox.UsuarioSessoesRevogadas"/>: não vai para cliente nenhum; o dispatcher
    /// derruba as conexões do hub do usuário (papel alterado, desativação, troca de senha; S-M08, S-M11).
    /// </summary>
    void EnfileirarSessoesRevogadas(Guid usuarioId);

    /// <summary>
    /// Descarta os eventos enfileirados que ainda NÃO foram salvos. Quem perde uma corrida de concorrência no <c>SaveChanges</c> chama isto, senão o evento já adicionado ao
    /// contexto sairia no próximo <c>SaveChanges</c>, sem a mudança de estado que o justifica (evento duplicado).
    /// </summary>
    void DescartarNaoSalvos()
    {
    }
}

public static class EventosDoOutbox
{
    /// <summary>Carga <c>{ usuarioId }</c>: o dispatcher derruba as conexões do hub desse usuário (S-M08).</summary>
    public const string UsuarioSessoesRevogadas = "UsuarioSessoesRevogadas";

    /// <summary>Carga <c>{ titulo, artista, iniciadoEmUtc }</c>: vai para todos os clientes do hub.</summary>
    public const string TocandoAgora = "TocandoAgora";

    /// <summary>
    /// Carga <c>{ programaId, titulo, status, locutorNome }</c> (02 §10): vai para todos. Gravado pelo Worker (início e fim por
    /// horário, em SQL) e pelos comandos encerrar e cancelar (E2-F01); o <c>status</c> é o nome do enum.
    /// </summary>
    public const string ProgramaStatusAlterado = "ProgramaStatusAlterado";

    /// <summary>Carga <c>{ noAr, programaId?, locutorNome? }</c> (06 "SignalR"): vai para todos. Gravado pelos eventos do harbor (E2-F06).</summary>
    public const string TransmissaoAoVivoAlterada = "TransmissaoAoVivoAlterada";

    /// <summary>
    /// Carga <c>{ tipo, falhas, janelaMinutos, locutorId?, locutorNome?, credencialAtiva? }</c> (06 "SignalR", E2-F12). Tipo <c>credencial</c>: vai para
    /// <c>admins</c> e para <c>usuario:{locutorId}</c>; tipo <c>global</c>: só <c>admins</c>. Nunca leva segredo, identificador nem IP.
    /// </summary>
    public const string AlertaHarborFalhas = "AlertaHarborFalhas";

    /// <summary>Carga <c>{ midiaId, statusSanitizacao, motivoRejeicao? }</c> (02 §10): para <c>locutores</c>. Gravado pelo Worker quando a mídia chega a Aprovado, Rejeitado ou Quarentena (E4-F05).</summary>
    public const string MidiaSanitizada = "MidiaSanitizada";

    /// <summary>Carga <c>PedidoDto</c> (02 §10): só para <c>locutores</c>. Leva nome e dedicatória do ouvinte, por isso nunca vai para todos.</summary>
    public const string PedidoCriado = "PedidoCriado";

    /// <summary>
    /// Carga <c>{ pedidoId, status, motivoRejeicao? }</c> (02 §10, 07 E3-D3): para <c>listener:{hash}</c> (quando o hash existe) e
    /// <c>locutores</c>. <c>status</c> é <c>Aprovado</c>, <c>Rejeitado</c> ou <c>Expirado</c> (o Worker emite o <c>Expirado</c>).
    /// </summary>
    public const string PedidoModerado = "PedidoModerado";

    /// <summary>Carga <c>{ pedidoId }</c> (02 §10): para <c>listener:{hash}</c> (quando o hash existe) e <c>locutores</c>.</summary>
    public const string PedidoTocado = "PedidoTocado";
}
