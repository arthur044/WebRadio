/* =====================================================================
   WebRadio — infra/sql/verificar-restauracao.sql
   Autor: Atlas (DBA) · tarefa E5-A01 (09-epico-5-tarefas.md, E5-D3/E5-D13)

   Teste de restauração do último backup, SEM tocar no banco em uso: restaura o .bak num banco temporário (WebRadio_verif, com
   MOVE para arquivos próprios), roda DBCC CHECKDB, mostra a contagem de linhas por tabela e apaga o temporário. É a verificação
   de verdade do backup (o BACKUP WITH CHECKSUM só prova que as páginas estavam íntegras ao ler; o RESTORE VERIFYONLY é fraco).
   Roda pelo `sa` (como o restore.sql), de preferência uma vez por mês pelo runbook, ou depois de cada backup se o disco der conta.
   Saída: linhas "TABELA schema.nome linhas" e, no fim, VERIFICACAO_OK. Qualquer erro do CHECKDB sai com código != 0 (sqlcmd -b);
   nesse caso o WebRadio_verif pode sobrar (o script o apaga no início da próxima rodada).
   ===================================================================== */

:on error exit
SET NOCOUNT ON;

IF DB_ID(N'WebRadio_verif') IS NOT NULL
BEGIN
    ALTER DATABASE WebRadio_verif SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE WebRadio_verif;
END
GO

DECLARE @dados nvarchar(260) = CONVERT(nvarchar(260), SERVERPROPERTY('InstanceDefaultDataPath'));
DECLARE @log   nvarchar(260) = CONVERT(nvarchar(260), SERVERPROPERTY('InstanceDefaultLogPath'));
DECLARE @sql nvarchar(max) = N'RESTORE DATABASE WebRadio_verif FROM DISK = N''/var/opt/mssql/backup/webradio.bak'' WITH CHECKSUM, '
    + N'MOVE N''WebRadio'' TO N''' + @dados + N'WebRadio_verif.mdf'', MOVE N''WebRadio_log'' TO N''' + @log + N'WebRadio_verif_log.ldf'', STATS = 100;';
EXEC (@sql);
GO

DBCC CHECKDB (WebRadio_verif) WITH NO_INFOMSGS, ALL_ERRORMSGS;
GO

SELECT N'TABELA ' + s.name + N'.' + t.name + N' ' + CONVERT(nvarchar(20), SUM(p.rows))
FROM WebRadio_verif.sys.tables AS t
JOIN WebRadio_verif.sys.schemas AS s ON s.schema_id = t.schema_id
JOIN WebRadio_verif.sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
GROUP BY s.name, t.name
ORDER BY s.name, t.name;
GO

ALTER DATABASE WebRadio_verif SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE WebRadio_verif;
GO
PRINT 'VERIFICACAO_OK';
GO
