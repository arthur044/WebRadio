using Microsoft.EntityFrameworkCore;
using WebRadio.Domain.Enums;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Trabalhos;

public sealed record ResultadoDasTransicoes(int Iniciados, int Finalizados);

public interface ITransicoesDaGrade
{
    Task<ResultadoDasTransicoes> ExecutarAsync(CancellationToken ct);
}

/// <summary>
/// E2-F03, Fluxo A passo 2 (00): <c>Agendado → AoVivo</c> (janela em curso) e <c>Agendado/AoVivo → Finalizado</c> (<c>FimUtc</c> passou),
/// com o relógio do SQL (<c>SYSUTCDATETIME()</c>, nunca o do contêiner: 00 §5), e <c>ProgramaStatusAlterado</c> no outbox na MESMA
/// transação, no envelope <c>{ "grupos": null, "dados": { programaId, titulo, status, locutorNome } }</c> (02 §10). O <c>OUTPUT</c>
/// traz <c>Titulo</c> e <c>LocutorId</c> nos dois casos e o <c>locutorNome</c> vem de um <c>JOIN</c> com <c>seg.Usuario</c>.
/// <para>
/// Idempotente por construção: cada <c>UPDATE</c> só pega linhas que ainda não mudaram, então rodar de novo (ou dois Workers ao
/// mesmo tempo: o segundo espera a trava de linha e reavalia o filtro) não gera evento repetido. Encerrar e cancelar já gravam o
/// próprio evento e deixam o programa fora do alcance destes <c>UPDATE</c>s (<c>Finalizado</c>/<c>Cancelado</c>).
/// </para>
/// <para>
/// <c>@agora</c> é <c>datetime2(3)</c>, não o <c>datetime2(0)</c> do rascunho do 00: <c>datetime2(0)</c> ARREDONDA, e o início
/// poderia ser antecipado em até meio segundo. As colunas <c>InicioUtc</c>/<c>FimUtc</c> são de precisão de segundo e o
/// <c>AtualizadoEmUtc</c> é <c>datetime2(3)</c>.
/// </para>
/// </summary>
public sealed class TransicoesDaGrade(RadioDbContext db) : ITransicoesDaGrade
{
    // Os números e os nomes do status vêm do enum, não de literais no SQL: se o enum mudar, o SQL acompanha (e o teste que fixa os
    // valores avisa que a migration e o índice filtrado IX_Programa_Worker precisam acompanhar também).
    private static readonly byte Agendado = (byte)StatusPrograma.Agendado;
    private static readonly byte AoVivo = (byte)StatusPrograma.AoVivo;
    private static readonly byte Finalizado = (byte)StatusPrograma.Finalizado;

    private static readonly string Sql = $"""
        SET XACT_ABORT ON;
        BEGIN TRAN;
        DECLARE @agora datetime2(3) = SYSUTCDATETIME();
        DECLARE @iniciados TABLE (Id uniqueidentifier, Titulo nvarchar(120), LocutorId uniqueidentifier);
        DECLARE @finalizados TABLE (Id uniqueidentifier, Titulo nvarchar(120), LocutorId uniqueidentifier);

        UPDATE grade.Programa SET Status = {AoVivo}, AtualizadoEmUtc = @agora
        OUTPUT inserted.Id, inserted.Titulo, inserted.LocutorId INTO @iniciados
        WHERE Status = {Agendado} AND InicioUtc <= @agora AND FimUtc > @agora;

        UPDATE grade.Programa SET Status = {Finalizado}, AtualizadoEmUtc = @agora
        OUTPUT inserted.Id, inserted.Titulo, inserted.LocutorId INTO @finalizados
        WHERE Status IN ({Agendado}, {AoVivo}) AND FimUtc <= @agora;

        INSERT infra.EventoOutbox (Tipo, Payload)
        SELECT 'ProgramaStatusAlterado',
               JSON_OBJECT('grupos': NULL,
                           'dados': JSON_OBJECT('programaId': LOWER(CONVERT(varchar(36), p.Id)), 'titulo': p.Titulo,
                                                'status': '{nameof(StatusPrograma.AoVivo)}', 'locutorNome': u.Nome))
        FROM @iniciados p JOIN seg.Usuario u ON u.Id = p.LocutorId
        UNION ALL
        SELECT 'ProgramaStatusAlterado',
               JSON_OBJECT('grupos': NULL,
                           'dados': JSON_OBJECT('programaId': LOWER(CONVERT(varchar(36), p.Id)), 'titulo': p.Titulo,
                                                'status': '{nameof(StatusPrograma.Finalizado)}', 'locutorNome': u.Nome))
        FROM @finalizados p JOIN seg.Usuario u ON u.Id = p.LocutorId;

        SELECT (SELECT COUNT(*) FROM @iniciados) AS Iniciados, (SELECT COUNT(*) FROM @finalizados) AS Finalizados;
        COMMIT;
        """;

    public async Task<ResultadoDasTransicoes> ExecutarAsync(CancellationToken ct)
    {
        var linhas = await db.Database.SqlQueryRaw<ResultadoDasTransicoes>(Sql).ToListAsync(ct);
        return linhas.Single();
    }
}
