using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Usuarios;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Interacao;

namespace WebRadio.Application.Features.Pedidos;

/// <summary>
/// <c>GET /pedidos/meus</c> (02 §5): os últimos 20 do dispositivo (<c>X-Listener-Id</c>) ou, com Bearer, os do usuário. Sem paginação:
/// devolve <c>PedidoDto[]</c>. O <c>PedidoDto</c> não tem hash nenhum; um ouvinte nunca vê pedido de outro dispositivo.
/// </summary>
public sealed record MeusPedidosQuery(Guid ListenerId, Guid? UsuarioId) : IRequest<IReadOnlyList<PedidoDto>>
{
    public const int Quantidade = 20;

    public override string ToString() => nameof(MeusPedidosQuery);
}

public sealed class MeusPedidosValidator : AbstractValidator<MeusPedidosQuery>
{
    public MeusPedidosValidator()
    {
        // Com Bearer o usuário basta; sem ele, o dispositivo é a identidade.
        RuleFor(x => x.ListenerId).NotEmpty().When(x => x.UsuarioId is null).OverridePropertyName("X-Listener-Id")
            .WithMessage("X-Listener-Id precisa ser um UUID válido.");
    }
}

public sealed class MeusPedidosHandler(IRadioDbContext db, IListenerIdentity identidade) : IRequestHandler<MeusPedidosQuery, IReadOnlyList<PedidoDto>>
{
    public async Task<IReadOnlyList<PedidoDto>> Handle(MeusPedidosQuery q, CancellationToken ct)
    {
        // Um índice por caminho: IX_PedidoMusica_Usuario (UsuarioId, CriadoEmUtc DESC) ou IX_PedidoMusica_Device (hash, CriadoEmUtc DESC).
        var consulta = db.PedidosMusica.AsNoTracking();
        if (q.UsuarioId is { } usuario)
        {
            consulta = consulta.Where(p => p.UsuarioId == usuario);
        }
        else
        {
            var dispositivo = identidade.DeviceHash(q.ListenerId);
            consulta = consulta.Where(p => p.ListenerDeviceHash == dispositivo);
        }

        var pedidos = await consulta.OrderByDescending(p => p.CriadoEmUtc).Take(MeusPedidosQuery.Quantidade).ToListAsync(ct);
        return [.. pedidos.Select(PedidoDto.De)];
    }
}

/// <summary>
/// <c>GET /pedidos?status=&amp;pagina=&amp;tamanho=</c> (02 §5, 07 E3-F02), para quem modera. <c>status</c> só <c>Pendente</c> (padrão, mais
/// antigos primeiro) ou <c>Aprovado</c> (últimas 24 h por <c>ModeradoEmUtc</c>, E3-D9); outro valor → 400. Cada ramo compara o status
/// com CONSTANTE (nunca parâmetro: com <c>@p0</c> o SQL Server não usa índice filtrado) e projeta só colunas do <c>INCLUDE</c> do índice.
/// </summary>
public sealed record ListarPedidosQuery(string? Status, int Pagina, int Tamanho) : IRequest<ResultadoPaginado<PedidoDto>>
{
    public static readonly TimeSpan JanelaDosAprovados = TimeSpan.FromHours(24);

    public StatusPedido StatusEfetivo =>
        string.Equals(Status, nameof(StatusPedido.Aprovado), StringComparison.OrdinalIgnoreCase) ? StatusPedido.Aprovado : StatusPedido.Pendente;
}

public sealed class ListarPedidosValidator : AbstractValidator<ListarPedidosQuery>
{
    public ListarPedidosValidator()
    {
        RuleFor(x => x.Status)
            .Must(s => s is null || string.Equals(s, nameof(StatusPedido.Pendente), StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(s, nameof(StatusPedido.Aprovado), StringComparison.OrdinalIgnoreCase))
            .WithMessage("O status só pode ser Pendente ou Aprovado.");
        RuleFor(x => x.Pagina).InclusiveBetween(1, 1_000_000).WithMessage("A página começa em 1.");
        RuleFor(x => x.Tamanho).InclusiveBetween(1, ResultadoPaginado<PedidoDto>.TamanhoMaximo)
            .WithMessage($"O tamanho da página vai de 1 a {ResultadoPaginado<PedidoDto>.TamanhoMaximo}.");
    }
}

public sealed class ListarPedidosHandler(IRadioDbContext db, IClock clock) : IRequestHandler<ListarPedidosQuery, ResultadoPaginado<PedidoDto>>
{
    public async Task<ResultadoPaginado<PedidoDto>> Handle(ListarPedidosQuery q, CancellationToken ct)
    {
        var pular = (q.Pagina - 1) * q.Tamanho;
        return q.StatusEfetivo == StatusPedido.Aprovado
            ? await AprovadosAsync(q, pular, ct)
            : await PendentesAsync(q, pular, ct);
    }

    /// <summary>A consulta da fila: <c>Status = 1</c> constante, ordem do <c>IX_PedidoMusica_Fila</c> (<c>CriadoEmUtc</c>) com desempate por <c>Id</c>.</summary>
    public static IQueryable<PedidoMusica> FilaDePendentes(IRadioDbContext db) =>
        db.PedidosMusica.AsNoTracking().Where(p => p.Status == StatusPedido.Pendente);

    /// <summary>Os aprovados das últimas 24 h: <c>Status = 2</c> constante, corte em <c>ModeradoEmUtc</c> (chave do <c>IX_PedidoMusica_Aprovados</c>).</summary>
    public static IQueryable<PedidoMusica> ListaDeAprovados(IRadioDbContext db, DateTime corte) =>
        db.PedidosMusica.AsNoTracking().Where(p => p.Status == StatusPedido.Aprovado && p.ModeradoEmUtc >= corte);

    private async Task<ResultadoPaginado<PedidoDto>> PendentesAsync(ListarPedidosQuery q, int pular, CancellationToken ct)
    {
        var fila = FilaDePendentes(db);
        var total = await fila.CountAsync(ct);
        var linhas = await fila.OrderBy(p => p.CriadoEmUtc).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.NomeOuvinte, p.TituloMusica, p.Artista, p.Mensagem, p.CriadoEmUtc, p.ProgramaId })
            .Skip(pular).Take(q.Tamanho).ToListAsync(ct);
        var itens = linhas.Select(l => new PedidoDto(l.Id, l.NomeOuvinte, l.TituloMusica, l.Artista, l.Mensagem, nameof(StatusPedido.Pendente),
            l.CriadoEmUtc, null, null, null, l.ProgramaId)).ToList();
        return new ResultadoPaginado<PedidoDto>(itens, total, q.Pagina, q.Tamanho);
    }

    private async Task<ResultadoPaginado<PedidoDto>> AprovadosAsync(ListarPedidosQuery q, int pular, CancellationToken ct)
    {
        // Corte de 24 h com o relógio do contêiner (parâmetro do LINQ): uma diferença de segundos para o SQL não muda a lista, e o plano é o mesmo
        // (seek de faixa no IX_PedidoMusica_Aprovados). Os gates de segurança (janela, expiração, retenção) usam SYSUTCDATETIME().
        var lista = ListaDeAprovados(db, clock.UtcNow - ListarPedidosQuery.JanelaDosAprovados);
        var total = await lista.CountAsync(ct);
        var linhas = await lista.OrderBy(p => p.ModeradoEmUtc).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.NomeOuvinte, p.TituloMusica, p.Artista, p.Mensagem, p.CriadoEmUtc, p.ModeradoEmUtc, p.ProgramaId })
            .Skip(pular).Take(q.Tamanho).ToListAsync(ct);
        var itens = linhas.Select(l => new PedidoDto(l.Id, l.NomeOuvinte, l.TituloMusica, l.Artista, l.Mensagem, nameof(StatusPedido.Aprovado),
            l.CriadoEmUtc, l.ModeradoEmUtc, null, null, l.ProgramaId)).ToList();
        return new ResultadoPaginado<PedidoDto>(itens, total, q.Pagina, q.Tamanho);
    }
}
