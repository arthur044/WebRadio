using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Grade;

namespace WebRadio.Application.Features.Programas;

/// <summary>Regras de grade compartilhadas pelos comandos de Programa (E2-F01).</summary>
internal static class GradeDeProgramas
{
    /// <summary>Quem pode apresentar: usuário ativo com papel Locutor ou Admin (01 §2.3). Devolve o nome.</summary>
    public static async Task<string> NomeDoLocutorAsync(IRadioDbContext db, Guid locutorId, CancellationToken ct)
    {
        var locutor = await db.Usuarios.AsNoTracking()
            .Where(u => u.Id == locutorId)
            .Select(u => new { u.Nome, u.Ativo, u.Role })
            .FirstOrDefaultAsync(ct);

        if (locutor is not { Ativo: true, Role: Role.Locutor or Role.Admin })
            throw new ValidationException([new ValidationFailure("locutorId", "O locutor precisa ser um usuário ativo com papel Locutor ou Admin.")]);
        return locutor.Nome;
    }

    /// <summary>
    /// Fluxo A, passo 1 (00): interseção com os programas <c>Agendado</c>/<c>AoVivo</c>, com os intervalos semiabertos
    /// <c>[Inicio, Fim)</c> (encostados não conflitam). O limite <c>InicioUtc &gt; inicio - 12 h</c> (duração máxima) transforma a
    /// consulta num <i>seek</i> de faixa no <c>IX_Programa_Grade</c>. SÓ vale sob <see cref="Travas.GradePrograma"/>; na edição,
    /// <paramref name="ignorarId"/> exclui o próprio programa.
    /// </summary>
    public static async Task GarantirSemConflitoAsync(IRadioDbContext db, DateTime inicioUtc, DateTime fimUtc, Guid? ignorarId, CancellationToken ct)
    {
        var limite = inicioUtc.Subtract(Programa.DuracaoMaxima);
        var conflitante = await db.Programas.AsNoTracking()
            .Where(p => (p.Status == StatusPrograma.Agendado || p.Status == StatusPrograma.AoVivo)
                        && p.InicioUtc < fimUtc && p.InicioUtc > limite && p.FimUtc > inicioUtc
                        && (ignorarId == null || p.Id != ignorarId))
            .OrderBy(p => p.InicioUtc)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct);

        if (conflitante is { } id) throw new ConflitoDeGradeException(id);
    }

    /// <summary>
    /// <c>ProgramaStatusAlterado</c> (02 §10) para todos. O <c>status</c> sai como o nome do enum (o mesmo do <c>ProgramaDto</c>)
    /// e a carga é a mesma que o Worker grava em SQL (E2-F03).
    /// </summary>
    public static void PublicarStatus(IOutbox outbox, Programa p, string locutorNome) =>
        outbox.EnfileirarParaTodos(EventosDoOutbox.ProgramaStatusAlterado,
            new { programaId = p.Id, titulo = p.Titulo, status = p.Status, locutorNome });

    /// <summary>
    /// Cancelar e encerrar disputam a linha com o Worker (que muda o status em SQL, sem a trava): se ele ganhar entre a leitura e
    /// o UPDATE, o RowVersion não bate e o EF devolve <see cref="VersaoDesatualizadaException"/>. Aqui não há <c>If-Match</c> do
    /// cliente, então uma segunda tentativa, já lendo o status novo, é o comportamento certo.
    /// </summary>
    public static async Task<T> ComUmaRepeticaoAsync<T>(Func<Task<T>> acao)
    {
        try
        {
            return await acao();
        }
        catch (VersaoDesatualizadaException)
        {
            return await acao();
        }
    }
}
