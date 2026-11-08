using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Usuarios;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Midia;

namespace WebRadio.Application.Features.Midias;

/// <summary>
/// <c>GET /midias?tipo=&amp;status=&amp;ativo=&amp;busca=&amp;pagina=&amp;tamanho=</c> (02 §6). <c>Ativo</c> só vale para o Admin: o Locutor vê só as ativas.
/// <c>Busca</c> é texto literal em Título, Artista e NomeOriginal (<c>%</c>, <c>_</c>, <c>[</c> e <c>\</c> são escapados).
/// </summary>
public sealed record ListarMidiasQuery(TipoMidia? Tipo, StatusSanitizacao? Status, bool? Ativo, string? Busca, int Pagina, int Tamanho, bool EhAdmin, Guid UsuarioId = default)
    : IRequest<ResultadoPaginado<MidiaDto>>;

public sealed class ListarMidiasValidator : AbstractValidator<ListarMidiasQuery>
{
    public const int TamanhoBusca = 200;

    public ListarMidiasValidator()
    {
        RuleFor(x => x.Pagina).InclusiveBetween(1, 1_000_000).WithMessage("A página começa em 1.");
        RuleFor(x => x.Tamanho).InclusiveBetween(1, ResultadoPaginado<MidiaDto>.TamanhoMaximo)
            .WithMessage($"O tamanho da página vai de 1 a {ResultadoPaginado<MidiaDto>.TamanhoMaximo}.");
        RuleFor(x => x.Busca).MaximumLength(TamanhoBusca).WithMessage($"A busca pode ter no máximo {TamanhoBusca} caracteres.");
        RuleFor(x => x.Tipo).IsInEnum().When(x => x.Tipo is not null);
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status is not null);
    }
}

internal static class ConsultaDeMidia
{
    /// <summary>Escapa o texto da busca para <c>LIKE ... ESCAPE '\'</c>: sem isso <c>100%</c> ou <c>[a]</c> viram curinga e casam o que não devem.</summary>
    public static string PadraoDeBusca(string busca) =>
        "%" + busca.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal) + "%";

    public sealed record Linha(ArquivoMidia Midia, string? Nome);

    public static IQueryable<Linha> ComNome(IRadioDbContext db, IQueryable<ArquivoMidia> midias) =>
        from m in midias
        join u in db.Usuarios on m.EnviadoPorUsuarioId equals u.Id into donos
        from dono in donos.DefaultIfEmpty()
        select new Linha(m, dono == null ? null : dono.Nome);
}

public sealed class ListarMidiasHandler(IRadioDbContext db) : IRequestHandler<ListarMidiasQuery, ResultadoPaginado<MidiaDto>>
{
    public async Task<ResultadoPaginado<MidiaDto>> Handle(ListarMidiasQuery q, CancellationToken ct)
    {
        var midias = db.ArquivosMidia.AsNoTracking();
        if (q.Tipo is { } tipo) midias = midias.Where(m => m.TipoMidia == tipo);
        if (q.Status is { } status) midias = midias.Where(m => m.StatusSanitizacao == status);
        if (!q.EhAdmin) midias = midias.Where(m => m.Ativo);
        else if (q.Ativo is { } ativo) midias = midias.Where(m => m.Ativo == ativo);
        if (!string.IsNullOrWhiteSpace(q.Busca))
        {
            var padrao = ConsultaDeMidia.PadraoDeBusca(q.Busca.Trim());
            midias = midias.Where(m => EF.Functions.Like(m.Titulo!, padrao, "\\") || EF.Functions.Like(m.Artista!, padrao, "\\") || EF.Functions.Like(m.NomeOriginal, padrao, "\\"));
        }

        var total = await midias.CountAsync(ct);
        var linhas = await ConsultaDeMidia.ComNome(db, midias.OrderByDescending(m => m.DataUploadUtc).ThenBy(m => m.Id).Skip((q.Pagina - 1) * q.Tamanho).Take(q.Tamanho))
            .ToListAsync(ct);
        // O join não preserva a ordem garantida: reaplica a mesma ordem em memória (a página já é pequena, ≤ 100).
        var itens = linhas.OrderByDescending(l => l.Midia.DataUploadUtc).ThenBy(l => l.Midia.Id).Select(l => MidiaDto.De(l.Midia, l.Nome, q.EhAdmin, q.UsuarioId)).ToList();
        return new ResultadoPaginado<MidiaDto>(itens, total, q.Pagina, q.Tamanho);
    }
}

/// <summary><c>GET /midias/{id}</c> + <c>ETag</c>. O Locutor não vê uma mídia desativada (404, como na lista).</summary>
public sealed record MidiaPorIdQuery(Guid Id, bool EhAdmin, Guid UsuarioId = default) : IRequest<MidiaDto>;

public sealed class MidiaPorIdHandler(IRadioDbContext db) : IRequestHandler<MidiaPorIdQuery, MidiaDto>
{
    public async Task<MidiaDto> Handle(MidiaPorIdQuery q, CancellationToken ct)
    {
        var linha = await ConsultaDeMidia.ComNome(db, db.ArquivosMidia.AsNoTracking().Where(m => m.Id == q.Id && (q.EhAdmin || m.Ativo))).FirstOrDefaultAsync(ct)
            ?? throw new NaoEncontradoException("Mídia");
        return MidiaDto.De(linha.Midia, linha.Nome, q.EhAdmin, q.UsuarioId);
    }
}

/// <summary>
/// <c>GET /midias/{id}/preview</c> (S-M07): a URL pré-assinada de GET (5 min) em <c>midia/</c>, com <c>response-content-type=audio/mpeg</c> e
/// <c>inline; filename*=UTF-8''{id}.mp3</c>. Só <c>Aprovado</c>: nunca há URL para a quarentena (a mídia fora desse status é 404, sem assinar nada).
/// </summary>
public sealed record PreviewDaMidiaQuery(Guid Id, bool EhAdmin) : IRequest<PreviewDto>;

public sealed class PreviewDaMidiaHandler(IRadioDbContext db, IClock clock, IObjectStorage? storage = null) : IRequestHandler<PreviewDaMidiaQuery, PreviewDto>
{
    public static readonly TimeSpan Validade = TimeSpan.FromMinutes(5);

    public async Task<PreviewDto> Handle(PreviewDaMidiaQuery q, CancellationToken ct)
    {
        var destino = storage ?? throw new InvalidOperationException("Storage não configurado.");
        var midia = await db.ArquivosMidia.AsNoTracking().Where(m => m.Id == q.Id && m.StatusSanitizacao == StatusSanitizacao.Aprovado && m.Bucket == Buckets.Midia && (q.EhAdmin || m.Ativo))
            .Select(m => new { m.Id, m.ChaveStorage }).FirstOrDefaultAsync(ct) ?? throw new NaoEncontradoException("Mídia");

        var url = await destino.UrlGetPreAssinadaAsync(Buckets.Midia, midia.ChaveStorage, Validade, "audio/mpeg", $"inline; filename*=UTF-8''{midia.Id}.mp3", ct);
        return new PreviewDto(url, clock.UtcNow + Validade);
    }
}
