using FluentValidation;
using MediatR;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Grade;

namespace WebRadio.Application.Features.Programas;

/// <summary>O corpo de criar e de editar: as mesmas regras de horário e de texto (01 §2.3).</summary>
public interface IProgramaCorpo
{
    string Titulo { get; }

    string? Descricao { get; }

    Guid LocutorId { get; }

    DateTime InicioUtc { get; }

    DateTime FimUtc { get; }
}

/// <summary>
/// As regras do Domain (<c>ArgumentException</c>) repetidas aqui para responder 400 com a lista de campos, em vez de 500. O Domain
/// continua sendo a última defesa. Horário em UTC (<c>Kind = Utc</c>, o que o JSON com sufixo <c>Z</c> produz) e sem fração de segundo.
/// </summary>
public abstract class ProgramaCorpoValidator<T> : AbstractValidator<T> where T : IProgramaCorpo
{
    protected ProgramaCorpoValidator(IClock clock)
    {
        RuleFor(x => x.Titulo).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Descricao).MaximumLength(1000);
        RuleFor(x => x.LocutorId).NotEmpty();

        RuleFor(x => x.InicioUtc).Must(EmUtc).WithMessage("O horário precisa estar em UTC (terminar em Z).")
            .Must(SemFracao).WithMessage("O horário tem precisão de segundo.");
        RuleFor(x => x.FimUtc).Must(EmUtc).WithMessage("O horário precisa estar em UTC (terminar em Z).")
            .Must(SemFracao).WithMessage("O horário tem precisão de segundo.");

        When(x => EmUtc(x.InicioUtc) && EmUtc(x.FimUtc), () =>
        {
            RuleFor(x => x.FimUtc).GreaterThan(x => x.InicioUtc).WithMessage("O fim precisa ser depois do início.");
            RuleFor(x => x).Must(x => x.FimUtc <= x.InicioUtc || (x.FimUtc - x.InicioUtc >= Programa.DuracaoMinima && x.FimUtc - x.InicioUtc <= Programa.DuracaoMaxima))
                .WithName("fimUtc").WithMessage("A duração precisa estar entre 5 minutos e 12 horas.");
            RuleFor(x => x.InicioUtc).Must(i => i >= clock.UtcNow - Programa.ToleranciaInicioNoPassado)
                .WithMessage("O início não pode estar mais de 1 minuto no passado.");
        });
    }

    private static bool EmUtc(DateTime d) => d.Kind == DateTimeKind.Utc;

    private static bool SemFracao(DateTime d) => d.Ticks % TimeSpan.TicksPerSecond == 0;
}

public sealed record CriarProgramaCommand(string Titulo, string? Descricao, Guid LocutorId, DateTime InicioUtc, DateTime FimUtc)
    : IRequest<ProgramaDto>, IProgramaCorpo;

public sealed class CriarProgramaValidator(IClock clock) : ProgramaCorpoValidator<CriarProgramaCommand>(clock);

/// <summary>
/// Fluxo A, passo 1: <c>sp_getapplock 'grade:programa'</c> exclusivo → interseção → INSERT, na mesma transação. Dois POST para o
/// mesmo horário: o segundo espera a trava, enxerga o primeiro e recebe o 409 (o intervalo é semiaberto: encostados passam).
/// </summary>
public sealed class CriarProgramaHandler(IRadioDbContext db, ITransacaoComTrava transacao, IClock clock)
    : IRequestHandler<CriarProgramaCommand, ProgramaDto>
{
    public Task<ProgramaDto> Handle(CriarProgramaCommand cmd, CancellationToken ct) =>
        transacao.ExecutarAsync(Travas.GradePrograma, async ct2 =>
        {
            var locutorNome = await GradeDeProgramas.NomeDoLocutorAsync(db, cmd.LocutorId, ct2);
            await GradeDeProgramas.GarantirSemConflitoAsync(db, cmd.InicioUtc, cmd.FimUtc, ignorarId: null, ct2);

            var programa = new Programa(Guid.CreateVersion7(), cmd.Titulo.Trim(), cmd.Descricao?.Trim(), cmd.LocutorId, cmd.InicioUtc, cmd.FimUtc, clock.UtcNow);
            db.Programas.Add(programa);
            await db.SaveChangesAsync(ct2);
            return ProgramaDto.De(programa, locutorNome);
        }, ct);
}
