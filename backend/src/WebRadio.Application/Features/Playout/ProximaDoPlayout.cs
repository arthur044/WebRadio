using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;

namespace WebRadio.Application.Features.Playout;

/// <summary>Seção <c>Playout:Rotacao</c> (08 E4-D4): os pesos dos tipos e a janela de repetição por tipo.</summary>
public sealed class RotacaoOptions
{
    public const string Secao = "Playout:Rotacao";

    public PesosDaRotacao Pesos { get; set; } = new();

    public JanelaDaRotacao JanelaMinutos { get; set; } = new();

    public int Peso(TipoMidia tipo) => tipo switch { TipoMidia.Musica => Pesos.Musica, TipoMidia.Vinheta => Pesos.Vinheta, TipoMidia.Comercial => Pesos.Comercial, _ => 0 };

    public TimeSpan Janela(TipoMidia tipo) => TimeSpan.FromMinutes(tipo == TipoMidia.Musica ? JanelaMinutos.Musica : JanelaMinutos.Outros);
}

public sealed class PesosDaRotacao
{
    public int Musica { get; set; } = 8;

    public int Vinheta { get; set; } = 2;

    public int Comercial { get; set; } = 1;
}

public sealed class JanelaDaRotacao
{
    /// <summary>Padrão 90 min para a música.</summary>
    public int Musica { get; set; } = 90;

    /// <summary>Padrão 20 min para vinheta e comercial.</summary>
    public int Outros { get; set; } = 20;
}

public sealed class RotacaoOptionsValidator : IValidateOptions<RotacaoOptions>
{
    public ValidateOptionsResult Validate(string? name, RotacaoOptions o)
    {
        var erros = new List<string>();
        if (o.Pesos.Musica < 0 || o.Pesos.Vinheta < 0 || o.Pesos.Comercial < 0) erros.Add("Playout:Rotacao:Pesos não pode ter peso negativo.");
        if (o.Pesos.Musica + o.Pesos.Vinheta + o.Pesos.Comercial == 0) erros.Add("Playout:Rotacao:Pesos precisa ter ao menos um peso positivo.");
        if (o.JanelaMinutos.Musica is < 0 or > 10_080 || o.JanelaMinutos.Outros is < 0 or > 10_080) erros.Add("Playout:Rotacao:JanelaMinutos deve estar entre 0 e 10080.");
        return erros.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(erros);
    }
}

/// <summary>02 §9: <c>{ midiaId, pedidoId, url, titulo, artista }</c>. <c>pedidoId</c> é nulo na rotação.</summary>
public sealed record ProximaFaixaDto(Guid MidiaId, Guid? PedidoId, string Url, string? Titulo, string? Artista)
{
    // A URL é pré-assinada: nunca vai ao log com {Obj}.
    public override string ToString() => nameof(ProximaFaixaDto);
}

/// <summary><c>GET /internal/playout/proxima</c> (02 §9): <paramref name="Instancia"/> é o UUID do boot do Liquidsoap, já validado pela borda.</summary>
public sealed record ProximaDoPlayoutQuery(Guid Instancia) : IRequest<ProximaFaixaDto?>;

/// <summary>
/// 08 E4-D5 e E4-D4. Primeiro o pedido (uma instrução SQL que captura e marca); sem pedido, a rotação da biblioteca: o tipo por peso, e dentro do tipo as três buscas do Atlas, parando na primeira que
/// devolve. Tipo sem candidata sai do sorteio; nenhum tipo com candidata → <c>null</c> (204: a playlist local assume). A faixa devolvida vira a "em voo".
/// </summary>
public sealed class ProximaDoPlayoutHandler(
    IFilaDoPlayout fila,
    IRadioDbContext db,
    IClock clock,
    IOptions<RotacaoOptions> opcoes,
    IFaixaEmVoo emVoo,
    IAleatorio aleatorio,
    IObjectStorage? storage = null) : IRequestHandler<ProximaDoPlayoutQuery, ProximaFaixaDto?>
{
    /// <summary>URL pré-assinada INTERNA (o Liquidsoap fala com o MinIO direto na rede <c>playout</c>): 30 min.</summary>
    public static readonly TimeSpan ValidadeDaUrl = TimeSpan.FromMinutes(30);

    public async Task<ProximaFaixaDto?> Handle(ProximaDoPlayoutQuery q, CancellationToken ct)
    {
        var agora = clock.UtcNow;

        if (await fila.CapturarPedidoAsync(q.Instancia, agora, ct) is { } pedido)
        {
            var dto = await MontarAsync(pedido.MidiaId, pedido.PedidoId, titulo: null, artista: null, ct);
            if (dto is not null)
            {
                await emVoo.DefinirAsync(pedido.MidiaId, ct);
                return dto;
            }
        }

        var voo = await emVoo.ObterAsync(ct);
        var o = opcoes.Value;
        var restantes = new List<TipoMidia>([TipoMidia.Musica, TipoMidia.Vinheta, TipoMidia.Comercial]);
        restantes.RemoveAll(t => o.Peso(t) <= 0);
        while (restantes.Count > 0)
        {
            var tipo = EscolherTipo(restantes, o);
            var corte = agora - o.Janela(tipo);
            var candidata = await fila.SortearForaDaJanelaAsync(tipo, aleatorio.ChaveDoSorteio(), corte, voo, ct)
                ?? await fila.MaisAntigaDaJanelaAsync(tipo, corte, voo, ct);
            if (candidata is not null)
            {
                var dto = await MontarAsync(candidata.Id, pedidoId: null, candidata.Titulo, candidata.Artista, ct);
                if (dto is not null)
                {
                    await emVoo.DefinirAsync(candidata.Id, ct);
                    return dto;
                }
            }

            restantes.Remove(tipo);
        }

        return null;
    }

    private TipoMidia EscolherTipo(List<TipoMidia> restantes, RotacaoOptions o)
    {
        var total = restantes.Sum(o.Peso);
        var sorteado = aleatorio.Proximo(0, total);
        foreach (var t in restantes)
        {
            sorteado -= o.Peso(t);
            if (sorteado < 0) return t;
        }

        return restantes[^1];
    }

    /// <summary>Carrega o bucket e a chave pela PK e assina a URL interna. Título e artista da rotação já vêm do índice; o do pedido vem da mídia.</summary>
    private async Task<ProximaFaixaDto?> MontarAsync(Guid midiaId, Guid? pedidoId, string? titulo, string? artista, CancellationToken ct)
    {
        var m = await db.ArquivosMidia.AsNoTracking().Where(x => x.Id == midiaId && x.StatusSanitizacao == StatusSanitizacao.Aprovado && x.Ativo)
            .Select(x => new { x.Bucket, x.ChaveStorage, x.Titulo, x.Artista }).FirstOrDefaultAsync(ct);
        if (m is null) return null;
        var url = await (storage ?? throw new InvalidOperationException("Storage não configurado.")).UrlGetPreAssinadaInternaAsync(m.Bucket, m.ChaveStorage, ValidadeDaUrl, ct);
        return new ProximaFaixaDto(midiaId, pedidoId, url, titulo ?? m.Titulo, artista ?? m.Artista);
    }
}
