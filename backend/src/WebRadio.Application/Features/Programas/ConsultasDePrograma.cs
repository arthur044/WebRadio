using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Grade;

namespace WebRadio.Application.Features.Programas;

/// <summary>Grade no intervalo <c>[DeUtc, AteUtc)</c> (02 §4): tudo que cruza o intervalo, ordenado por <c>InicioUtc</c>.</summary>
public sealed record ListarProgramasQuery(DateTime DeUtc, DateTime AteUtc, bool IncluirCancelados) : IRequest<IReadOnlyList<ProgramaDto>>
{
    public static readonly TimeSpan IntervaloMaximo = TimeSpan.FromDays(31);
}

public sealed class ListarProgramasValidator : AbstractValidator<ListarProgramasQuery>
{
    public ListarProgramasValidator()
    {
        RuleFor(x => x.DeUtc).Must(d => d.Kind == DateTimeKind.Utc).WithMessage("O horário precisa estar em UTC (terminar em Z).");
        RuleFor(x => x.AteUtc).Must(d => d.Kind == DateTimeKind.Utc).WithMessage("O horário precisa estar em UTC (terminar em Z).");
        RuleFor(x => x.AteUtc).GreaterThan(x => x.DeUtc).WithMessage("ateUtc precisa ser depois de deUtc.");
        RuleFor(x => x).Must(x => x.AteUtc <= x.DeUtc || x.AteUtc - x.DeUtc <= ListarProgramasQuery.IntervaloMaximo)
            .WithName("ateUtc").WithMessage("O intervalo pode ter no máximo 31 dias.");
    }
}

public sealed class ListarProgramasHandler(IRadioDbContext db) : IRequestHandler<ListarProgramasQuery, IReadOnlyList<ProgramaDto>>
{
    public async Task<IReadOnlyList<ProgramaDto>> Handle(ListarProgramasQuery q, CancellationToken ct)
    {
        // O limite InicioUtc > de - 12 h (duração máxima) mantém a consulta um seek de faixa no IX_Programa_Grade.
        var limite = q.DeUtc.Subtract(Programa.DuracaoMaxima);
        var consulta = db.Programas.AsNoTracking()
            .Where(p => p.InicioUtc < q.AteUtc && p.InicioUtc > limite && p.FimUtc > q.DeUtc);
        if (!q.IncluirCancelados) consulta = consulta.Where(p => p.Status != StatusPrograma.Cancelado);

        return await Projetar(db, consulta.OrderBy(p => p.InicioUtc), ct);
    }

    internal static async Task<List<ProgramaDto>> Projetar(IRadioDbContext db, IQueryable<Programa> consulta, CancellationToken ct)
    {
        var linhas = await (from p in consulta
                            join u in db.Usuarios.AsNoTracking() on p.LocutorId equals u.Id
                            select new { Programa = p, u.Nome }).ToListAsync(ct);
        return [.. linhas.Select(l => ProgramaDto.De(l.Programa, l.Nome))];
    }
}

public sealed record ProgramaAoVivoQuery : IRequest<ProgramaDto?>;

/// <summary>O programa com <c>Status = AoVivo</c> (quem muda o status é o Worker, E2-F03), ou nulo.</summary>
public sealed class ProgramaAoVivoHandler(IRadioDbContext db) : IRequestHandler<ProgramaAoVivoQuery, ProgramaDto?>
{
    public async Task<ProgramaDto?> Handle(ProgramaAoVivoQuery q, CancellationToken ct) =>
        (await ListarProgramasHandler.Projetar(db, db.Programas.AsNoTracking().Where(p => p.Status == StatusPrograma.AoVivo).OrderBy(p => p.InicioUtc).Take(1), ct))
        .FirstOrDefault();
}

public sealed record ProgramaPorIdQuery(Guid Id) : IRequest<ProgramaDto>;

public sealed class ProgramaPorIdHandler(IRadioDbContext db) : IRequestHandler<ProgramaPorIdQuery, ProgramaDto>
{
    public async Task<ProgramaDto> Handle(ProgramaPorIdQuery q, CancellationToken ct) =>
        (await ListarProgramasHandler.Projetar(db, db.Programas.AsNoTracking().Where(p => p.Id == q.Id), ct)).FirstOrDefault()
        ?? throw new NaoEncontradoException("Programa");
}
