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

    /// <summary>
    /// Vários recursos na mesma transação, adquiridos SEMPRE em ordem ordinal (duas requisições com os mesmos recursos nunca se travam
    /// em ordem cruzada). <paramref name="espera"/> vale para cada trava.
    /// </summary>
    Task<T> ExecutarAsync<T>(IReadOnlyList<string> recursos, TimeSpan espera, Func<CancellationToken, Task<T>> acao, CancellationToken ct);
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

    /// <summary>Mesma trava para o IP (rajada com X-Listener-Id diferentes passaria a contagem do IP ao mesmo tempo) e para o usuário logado.</summary>
    public static string PedidoDoIp(byte[] ipHash) => "pedido:ip:" + Convert.ToHexString(ipHash);

    public static string PedidoDoUsuario(string chaveOpaca) => "pedido:usr:" + chaveOpaca;

    /// <summary>
    /// 08 E4-F02 (S-M06): serializa contar os uploads abertos do usuário e inserir o novo. Sem a trava, 20 <c>POST</c> paralelos veriam "0 abertos" juntos e
    /// passariam do limite de 5. Um applock por usuário no lugar de <c>UPDLOCK, HOLDLOCK</c> na tabela: o range lock sem índice por usuário varre (e trava) as linhas dos outros.
    /// </summary>
    public static string UploadDeMidiaDoUsuario(string chaveOpaca) => "midia:upload:" + chaveOpaca;
}
