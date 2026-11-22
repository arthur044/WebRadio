using FluentValidation;
using MediatR;
using WebRadio.Domain.Enums;

namespace WebRadio.Application.Features.Relatorios;

/// <summary>02 §11: <c>{ midiaId, titulo, artista, tipoMidia, reproducoes }</c>, da mais tocada para a menos.</summary>
public sealed record MidiaMaisTocadaDto(Guid MidiaId, string? Titulo, string? Artista, TipoMidia TipoMidia, int Reproducoes);

/// <summary><c>rel.usp_MaisTocadas</c> (08 E4-D12): a agregação roda no SQL, que aplica o <c>TOP</c> antes do <c>JOIN</c>. Implementada na Infrastructure.</summary>
public interface IRelatorioDeReproducoes
{
    Task<IReadOnlyList<MidiaMaisTocadaDto>> MaisTocadasAsync(DateTime deUtc, DateTime ateUtc, int top, CancellationToken ct);
}

/// <summary><c>GET /relatorios/mais-tocadas?de=&amp;ate=&amp;top=</c> (Admin): <c>ate</c> exclusivo, período de até 366 dias, <c>top</c> de 1 a 100 (padrão 20).</summary>
public sealed record MaisTocadasQuery(DateTime? De, DateTime? Ate, int Top = MaisTocadasQuery.TopPadrao) : IRequest<IReadOnlyList<MidiaMaisTocadaDto>>
{
    public const int TopPadrao = 20;
}

public sealed class MaisTocadasValidator : AbstractValidator<MaisTocadasQuery>
{
    public static readonly TimeSpan PeriodoMaximo = TimeSpan.FromDays(366);

    /// <summary>
    /// A data da query pode vir sem offset (<c>?de=2026-11-01</c>, Kind <c>Unspecified</c>): é UTC, nunca a hora local do processo. Só o Kind <c>Local</c> (offset explícito) é convertido.
    /// </summary>
    internal static DateTime ComoUtc(DateTime d) => d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    internal static DateTime NoSegundo(DateTime d)
    {
        var utc = ComoUtc(d);
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    public MaisTocadasValidator()
    {
        RuleFor(x => x.De).NotNull().WithMessage("Informe de (UTC).");
        RuleFor(x => x.Ate).NotNull().WithMessage("Informe ate (UTC, exclusivo).");
        // A procedure trabalha em segundos (datetime2(0)): a conta é feita nos valores já truncados, para um período de menos de 1 s ser 400 e não o THROW 50101 (500).
        RuleFor(x => x).Must(x => x.De is null || x.Ate is null || NoSegundo(x.Ate.Value) > NoSegundo(x.De.Value)).WithName("ate").WithMessage("ate precisa ser pelo menos 1 segundo depois de de.");
        RuleFor(x => x).Must(x => x.De is null || x.Ate is null || NoSegundo(x.Ate.Value) <= NoSegundo(x.De.Value) + PeriodoMaximo).WithName("ate").WithMessage("O período máximo é de 366 dias.");
        RuleFor(x => x.Top).InclusiveBetween(1, 100).WithMessage("top vai de 1 a 100.");
    }
}

public sealed class MaisTocadasHandler(IRelatorioDeReproducoes relatorio) : IRequestHandler<MaisTocadasQuery, IReadOnlyList<MidiaMaisTocadaDto>>
{
    public Task<IReadOnlyList<MidiaMaisTocadaDto>> Handle(MaisTocadasQuery q, CancellationToken ct) =>
        relatorio.MaisTocadasAsync(MaisTocadasValidator.NoSegundo(q.De!.Value), MaisTocadasValidator.NoSegundo(q.Ate!.Value), q.Top, ct);
}
