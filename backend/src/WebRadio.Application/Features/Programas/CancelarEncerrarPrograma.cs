using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Grade;

namespace WebRadio.Application.Features.Programas;

public sealed record CancelarProgramaCommand(Guid Id) : IRequest<ProgramaDto>;

/// <summary>
/// Só <c>Agendado</c> (Admin). Grava <c>ProgramaStatusAlterado</c> no outbox NA MESMA transação: o Worker só enxerga
/// <c>Agendado</c> e <c>AoVivo</c>, então um cancelado nunca teria o evento por ele, e a tela do público precisa trocar sem refresh.
/// </summary>
public sealed class CancelarProgramaHandler(IRadioDbContext db, ITransacaoComTrava transacao, IOutbox outbox, IClock clock)
    : IRequestHandler<CancelarProgramaCommand, ProgramaDto>
{
    public Task<ProgramaDto> Handle(CancelarProgramaCommand cmd, CancellationToken ct) =>
        GradeDeProgramas.ComUmaRepeticaoAsync(() => transacao.ExecutarAsync(Travas.GradePrograma, async ct2 =>
        {
            var programa = await db.Programas.FirstOrDefaultAsync(p => p.Id == cmd.Id, ct2)
                ?? throw new NaoEncontradoException("Programa");
            var locutorNome = await NomeAsync(db, programa, ct2);

            programa.Cancelar(clock.UtcNow);
            GradeDeProgramas.PublicarStatus(outbox, programa, locutorNome);
            await db.SaveChangesAsync(ct2);
            return ProgramaDto.De(programa, locutorNome);
        }, ct));

    internal static Task<string> NomeAsync(IRadioDbContext db, Programa p, CancellationToken ct) =>
        db.Usuarios.AsNoTracking().Where(u => u.Id == p.LocutorId).Select(u => u.Nome).FirstAsync(ct);
}

/// <summary><c>SolicitanteEhAdmin</c>: Admin encerra qualquer um; Locutor só o próprio (02 §1).</summary>
public sealed record EncerrarProgramaCommand(Guid Id, Guid SolicitanteId, bool SolicitanteEhAdmin) : IRequest<ProgramaDto>;

/// <summary>
/// <c>AoVivo</c> ou <c>Agendado</c> com a janela em curso (Worker parado). <c>FimUtc = máx(agora, InicioUtc + 1 s)</c>.
/// Grava <c>ProgramaStatusAlterado</c> na mesma transação; o Worker não gera um segundo (o programa já é <c>Finalizado</c>).
/// </summary>
public sealed class EncerrarProgramaHandler(IRadioDbContext db, ITransacaoComTrava transacao, IOutbox outbox, IClock clock)
    : IRequestHandler<EncerrarProgramaCommand, ProgramaDto>
{
    public Task<ProgramaDto> Handle(EncerrarProgramaCommand cmd, CancellationToken ct) =>
        GradeDeProgramas.ComUmaRepeticaoAsync(() => transacao.ExecutarAsync(Travas.GradePrograma, async ct2 =>
        {
            var programa = await db.Programas.FirstOrDefaultAsync(p => p.Id == cmd.Id, ct2)
                ?? throw new NaoEncontradoException("Programa");
            if (!cmd.SolicitanteEhAdmin && programa.LocutorId != cmd.SolicitanteId)
                throw new AcessoNegadoException("Só o locutor do programa ou um Admin pode encerrá-lo.");
            var locutorNome = await CancelarProgramaHandler.NomeAsync(db, programa, ct2);

            programa.Encerrar(clock.UtcNow);
            GradeDeProgramas.PublicarStatus(outbox, programa, locutorNome);
            await db.SaveChangesAsync(ct2);
            return ProgramaDto.De(programa, locutorNome);
        }, ct));
}
