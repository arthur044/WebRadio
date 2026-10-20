using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Harbor;

/// <summary>
/// 06 "Autorização do ao vivo: pela janela". SQL explícito: a hora é a do servidor (<c>SYSUTCDATETIME()</c>), nunca a do
/// <c>IClock</c>, e o formato da consulta é o que o Atlas mede no E2-A02 (consulta 3).
/// </summary>
public sealed class ConsultasDoHarbor(RadioDbContext db) : IConsultasDoHarbor
{
    public Task<CredencialDoHarbor?> CredencialAtivaAsync(Guid identificador, CancellationToken ct) =>
        db.CredenciaisTransmissao.AsNoTracking()
            .Where(c => c.Identificador == identificador && c.RevogadaEmUtc == null)
            .Select(c => new CredencialDoHarbor(c.Id, c.Salt, c.SegredoHash))
            .FirstOrDefaultAsync(ct);

    public async Task<JanelaDoHarbor?> JanelaAsync(Guid identificador, CancellationToken ct)
    {
        // Status NOT IN (3, 4) = Finalizado, Cancelado; Role IN (1, 2) = Admin, Locutor (01 §1). O limite de 12 h (duração máxima)
        // em InicioUtc mantém um seek de faixa no IX_Programa_Grade, como a interseção da grade: o filtro de Status impede o
        // IX_Programa_Worker. Programas de um locutor nunca se sobrepõem (D7), então há no máximo um.
        var linhas = await db.Database.SqlQuery<LinhaDaJanela>($"""
            SELECT TOP (1) p.Id AS ProgramaId, p.FimUtc
            FROM seg.CredencialTransmissao AS c
            JOIN seg.Usuario AS u ON u.Id = c.UsuarioId
            JOIN grade.Programa AS p ON p.LocutorId = c.UsuarioId
            WHERE c.Identificador = {identificador}
              AND c.RevogadaEmUtc IS NULL
              AND u.Ativo = 1
              AND u.Role IN (1, 2)
              AND p.InicioUtc <= SYSUTCDATETIME()
              AND p.InicioUtc > DATEADD(HOUR, -12, SYSUTCDATETIME())
              AND p.FimUtc > SYSUTCDATETIME()
              AND p.Status NOT IN (3, 4)
            ORDER BY p.InicioUtc DESC
            """).ToListAsync(ct);

        return linhas is [var l, ..] ? new JanelaDoHarbor(l.ProgramaId, DateTime.SpecifyKind(l.FimUtc, DateTimeKind.Utc)) : null;
    }

    public Task RegistrarUsoAsync(Guid credencialId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"UPDATE seg.CredencialTransmissao SET UltimoUsoEmUtc = SYSUTCDATETIME() WHERE Id = {credencialId}", ct);

    private sealed record LinhaDaJanela(Guid ProgramaId, DateTime FimUtc);
}
