using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Erros;

namespace WebRadio.Application.Features.Programas;

/// <summary><c>Versao</c> é o RowVersion que o cliente leu (cabeçalho <c>If-Match</c>).</summary>
public sealed record EditarProgramaCommand(Guid Id, string Titulo, string? Descricao, Guid LocutorId, DateTime InicioUtc, DateTime FimUtc, byte[] Versao)
    : IRequest<ProgramaDto>, IProgramaCorpo;

public sealed class EditarProgramaValidator(IClock clock) : ProgramaCorpoValidator<EditarProgramaCommand>(clock);

/// <summary>
/// Só <c>Agendado</c>. Mudar o horário reabre a checagem de sobreposição sob a mesma trava da criação, <b>excluindo o próprio
/// <c>Id</c></b> (senão o programa conflitaria com o horário antigo dele mesmo).
/// </summary>
public sealed class EditarProgramaHandler(IRadioDbContext db, ITransacaoComTrava transacao, IClock clock)
    : IRequestHandler<EditarProgramaCommand, ProgramaDto>
{
    public Task<ProgramaDto> Handle(EditarProgramaCommand cmd, CancellationToken ct) =>
        transacao.ExecutarAsync(Travas.GradePrograma, async ct2 =>
        {
            var programa = await db.Programas.FirstOrDefaultAsync(p => p.Id == cmd.Id, ct2)
                ?? throw new NaoEncontradoException("Programa");
            if (!programa.RowVersion.AsSpan().SequenceEqual(cmd.Versao)) throw new VersaoDesatualizadaException();

            var locutorNome = await GradeDeProgramas.NomeDoLocutorAsync(db, cmd.LocutorId, ct2);
            await GradeDeProgramas.GarantirSemConflitoAsync(db, cmd.InicioUtc, cmd.FimUtc, ignorarId: cmd.Id, ct2);

            programa.Editar(cmd.Titulo.Trim(), cmd.Descricao?.Trim(), cmd.LocutorId, cmd.InicioUtc, cmd.FimUtc, clock.UtcNow);
            await db.SaveChangesAsync(ct2);
            return ProgramaDto.De(programa, locutorNome);
        }, ct);
}
