using WebRadio.Domain.Enums;

namespace WebRadio.Application.Abstracoes;

/// <summary>O pedido que a <c>proxima</c> acabou de entregar à instância do Liquidsoap (já marcado no banco).</summary>
public sealed record PedidoParaTocar(Guid PedidoId, Guid MidiaId);

/// <summary>Uma candidata da rotação: o título e o artista vêm do índice (<c>INCLUDE</c>), sem tocar a tabela.</summary>
public sealed record CandidataDaRotacao(Guid Id, string? Titulo, string? Artista);

/// <summary>
/// SQL do playout que o Atlas mediu (<c>docs/pareceres/e4-a01a-consultas-playout.md</c>): o texto e os hints <c>INDEX(...)</c> fazem parte do contrato. Implementada na Infrastructure.
/// </summary>
public interface IFilaDoPlayout
{
    /// <summary>
    /// E4-D5, numa só instrução: o pedido <c>Aprovado</c> com mídia <c>Aprovado</c> e <c>Ativo</c> mais antigo, moderado há no máximo 24 h, ainda não entregue ou entregue a OUTRA instância do
    /// Liquidsoap, com <c>UPDLOCK, READPAST</c> (nunca o mesmo pedido para duas chamadas). Marca <c>EnviadoPlayoutEmUtc</c> e <c>EnviadoPlayoutInstancia</c>. <c>null</c> = nenhum elegível.
    /// </summary>
    Task<PedidoParaTocar?> CapturarPedidoAsync(Guid instancia, DateTime agora, CancellationToken ct);

    /// <summary>
    /// Quantas candidatas a rotação pede de uma vez e entre quantas sorteia (E4-A01b, 08 v1.8: K = 5). A chave fixa (K = 1) deixava a mídia logo depois de um "buraco" de chaves até
    /// 12,8x mais provável que a média em 50 mil faixas (3,85x em 300); com K = 5 o pior caso cai para 3,6x (50 mil) e 2,6x (até mil), a ~53 a 68 leituras por chamada (critério: 70).
    /// </summary>
    const int CandidatasPorSorteio = 5;

    /// <summary>
    /// E4-D4, buscas 1 e 2: até <see cref="CandidatasPorSorteio"/> mídias <c>Aprovado</c> e <c>Ativo</c> do tipo, fora da janela de repetição e diferentes da faixa em voo, a partir de
    /// <paramref name="sorteio"/> (<c>Sorteio &gt;= @r</c>, da menor chave para a maior) e, se vierem menos, completando com as do início (<c>Sorteio &lt; @r</c>). Lista vazia = nenhuma candidata
    /// (cai na busca 3). A ESCOLHA entre elas é de quem chama (<see cref="IAleatorio"/>): a fila só lê, e o teste fica determinístico.
    /// </summary>
    Task<IReadOnlyList<CandidataDaRotacao>> CandidatasForaDaJanelaAsync(TipoMidia tipo, int sorteio, DateTime corteDaJanela, Guid emVoo, CancellationToken ct);

    /// <summary>E4-D4, busca 3 (fallback): nenhuma fora da janela; a que tocou há mais tempo DENTRO do conjunto recente, diferente da faixa em voo.</summary>
    Task<CandidataDaRotacao?> MaisAntigaDaJanelaAsync(TipoMidia tipo, DateTime corteDaJanela, Guid emVoo, CancellationToken ct);
}

/// <summary>A última faixa que a <c>proxima</c> devolveu: ela ainda não está em <c>Reproducao</c> (a linha só nasce no <c>FaixaIniciada</c>), então a rotação a exclui por aqui (E4-D4).</summary>
public interface IFaixaEmVoo
{
    Task DefinirAsync(Guid midiaId, CancellationToken ct);

    /// <summary><c>Guid.Empty</c> quando não há faixa em voo.</summary>
    Task<Guid> ObterAsync(CancellationToken ct);
}

/// <summary>Aleatório injetável: o teste da rotação precisa ser determinístico.</summary>
public interface IAleatorio
{
    /// <summary>Inteiro em <c>[minimo, maximoExclusivo)</c>.</summary>
    int Proximo(int minimo, int maximoExclusivo);

    /// <summary>A chave do sorteio da rotação, em todo o intervalo do <c>int</c>.</summary>
    int ChaveDoSorteio();
}
