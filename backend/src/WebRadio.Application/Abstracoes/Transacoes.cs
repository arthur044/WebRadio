namespace WebRadio.Application.Abstracoes;

/// <summary>
/// Roda <c>acao</c> numa transação que segura um <c>sp_getapplock</c> exclusivo até o commit (D7, D22). O bloco inteiro é
/// reexecutado no retry do EF: sem efeito colateral fora do banco e sem entidade carregada antes dele.
/// </summary>
public interface ITransacaoComTrava
{
    Task<T> ExecutarAsync<T>(string recurso, Func<CancellationToken, Task<T>> acao, CancellationToken ct);

    /// <summary>
    /// Igual, mas esperando a trava no máximo <paramref name="espera"/>; estourou, lança <see cref="Domain.Erros.TravaOcupadaException"/>
    /// (quem pede uma espera curta decide a resposta). O bloco é reexecutado no retry do EF, como no outro método.
    /// </summary>
    Task<T> ExecutarAsync<T>(string recurso, TimeSpan espera, Func<CancellationToken, Task<T>> acao, CancellationToken ct);
}

/// <summary>Sonda de saúde do banco (<c>/health/ready</c>): a Api não conhece o EF.</summary>
public interface ISondaDoBanco
{
    Task<bool> ConectaAsync(CancellationToken ct);
}

public static class Travas
{
    /// <summary>Serializa tudo que conta ou altera Admins: o "último Admin ativo" só é confiável sob esta trava.</summary>
    public const string Admins = "seg:admins";

    /// <summary>D7: serializa criar/editar programa. A consulta de interseção só vale sob esta trava (Fluxo A, passo 1).</summary>
    public const string GradePrograma = "grade:programa";

    /// <summary>
    /// 07 E3-F01: serializa o fallback SQL do throttle de pedido por dispositivo (verifica-e-insere; sem a trava duas requisições
    /// simultâneas passam). O recurso é derivado do hash do dispositivo: nunca leva o <c>listenerId</c>.
    /// </summary>
    public static string PedidoDoDispositivo(byte[] deviceHash) => "pedido:dev:" + Convert.ToHexString(deviceHash);
}

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

/// <summary>
/// Versão de segurança atual do usuário (claim <c>sv</c>), com cache de 15 s por instância: é consultada em TODA
/// requisição autenticada. <see cref="Esquecer"/> faz a mudança valer na hora nesta instância; nas outras, em até 15 s.
/// </summary>
public interface IVersaoDeSeguranca
{
    /// <summary>null quando o usuário não existe ou está inativo.</summary>
    Task<int?> AtualAsync(Guid usuarioId, CancellationToken ct);

    void Esquecer(Guid usuarioId);
}
