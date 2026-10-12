namespace WebRadio.Application.Abstracoes;

/// <summary>
/// Roda <c>acao</c> numa transação que segura um <c>sp_getapplock</c> exclusivo até o commit (D7, D22). O bloco inteiro é
/// reexecutado no retry do EF: sem efeito colateral fora do banco e sem entidade carregada antes dele.
/// </summary>
public interface ITransacaoComTrava
{
    Task<T> ExecutarAsync<T>(string recurso, Func<CancellationToken, Task<T>> acao, CancellationToken ct);
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
