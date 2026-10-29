using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Usuarios;
using WebRadio.Domain.Erros;

namespace WebRadio.Application.Features.Divulgacoes;

/// <summary>
/// <c>GET /divulgacoes/ativas</c> (público): <c>Ativo = 1</c> como CONSTANTE (o <c>IX_Divulgacao_Ativas</c> é filtrado), a janela com o relógio do
/// servidor, <c>ORDER BY Prioridade DESC, Id</c> (desempate determinístico) e só colunas do <c>INCLUDE</c> do índice.
/// </summary>
public sealed record DivulgacoesAtivasQuery : IRequest<IReadOnlyList<DivulgacaoDto>>;

public sealed class DivulgacoesAtivasHandler(IRadioDbContext db, IClock clock, IObjectStorage? storage = null)
    : IRequestHandler<DivulgacoesAtivasQuery, IReadOnlyList<DivulgacaoDto>>
{
    /// <summary>A consulta, para o teste conferir o SQL gerado.</summary>
    public static IQueryable<Domain.Interacao.Divulgacao> Ativas(IRadioDbContext db, DateTime agora) =>
        db.Divulgacoes.AsNoTracking().Where(d => d.Ativo
            && (d.InicioExibicaoUtc == null || d.InicioExibicaoUtc <= agora)
            && (d.FimExibicaoUtc == null || agora < d.FimExibicaoUtc));

    public async Task<IReadOnlyList<DivulgacaoDto>> Handle(DivulgacoesAtivasQuery q, CancellationToken ct)
    {
        var linhas = await Ativas(db, clock.UtcNow).OrderByDescending(d => d.Prioridade).ThenBy(d => d.Id)
            .Select(d => new { d.Id, d.Prioridade, d.Titulo, d.Mensagem, d.ImagemChaveStorage, d.LinkDestino, d.InicioExibicaoUtc, d.FimExibicaoUtc })
            .ToListAsync(ct);
        return [.. linhas.Select(l => new DivulgacaoDto(l.Id, l.Titulo, l.Mensagem, l.ImagemChaveStorage,
            DivulgacaoDto.UrlDaImagem(l.ImagemChaveStorage, storage), l.LinkDestino, l.Prioridade, l.InicioExibicaoUtc, l.FimExibicaoUtc, true, ""))];
    }
}

/// <summary><c>GET /divulgacoes?pagina=&amp;tamanho=</c> (Admin): todas, ativas ou não, da mais prioritária para a menos.</summary>
public sealed record ListarDivulgacoesQuery(int Pagina, int Tamanho) : IRequest<ResultadoPaginado<DivulgacaoDto>>;

public sealed class ListarDivulgacoesValidator : AbstractValidator<ListarDivulgacoesQuery>
{
    public ListarDivulgacoesValidator()
    {
        RuleFor(x => x.Pagina).InclusiveBetween(1, 1_000_000).WithMessage("A página começa em 1.");
        RuleFor(x => x.Tamanho).InclusiveBetween(1, ResultadoPaginado<DivulgacaoDto>.TamanhoMaximo)
            .WithMessage($"O tamanho da página vai de 1 a {ResultadoPaginado<DivulgacaoDto>.TamanhoMaximo}.");
    }
}

public sealed class ListarDivulgacoesHandler(IRadioDbContext db, IObjectStorage? storage = null)
    : IRequestHandler<ListarDivulgacoesQuery, ResultadoPaginado<DivulgacaoDto>>
{
    public async Task<ResultadoPaginado<DivulgacaoDto>> Handle(ListarDivulgacoesQuery q, CancellationToken ct)
    {
        var consulta = db.Divulgacoes.AsNoTracking();
        var total = await consulta.CountAsync(ct);
        var itens = await consulta.OrderByDescending(d => d.Prioridade).ThenBy(d => d.Id)
            .Skip((q.Pagina - 1) * q.Tamanho).Take(q.Tamanho).ToListAsync(ct);
        return new ResultadoPaginado<DivulgacaoDto>([.. itens.Select(d => DivulgacaoDto.De(d, storage))], total, q.Pagina, q.Tamanho);
    }
}

/// <summary><c>GET /divulgacoes/{id}</c> (Admin) → 200 + <c>ETag</c>; inexistente → 404.</summary>
public sealed record DivulgacaoPorIdQuery(Guid Id) : IRequest<DivulgacaoDto>;

public sealed class DivulgacaoPorIdHandler(IRadioDbContext db, IObjectStorage? storage = null) : IRequestHandler<DivulgacaoPorIdQuery, DivulgacaoDto>
{
    public async Task<DivulgacaoDto> Handle(DivulgacaoPorIdQuery q, CancellationToken ct) =>
        DivulgacaoDto.De(await db.Divulgacoes.AsNoTracking().FirstOrDefaultAsync(d => d.Id == q.Id, ct) ?? throw new NaoEncontradoException("Divulgação"), storage);
}
