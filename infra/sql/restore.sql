/* =====================================================================
   WebRadio — infra/sql/restore.sql
   Autor: Atlas (DBA) · tarefa E5-A01 (09-epico-5-tarefas.md, E5-D13)

   RESTORE do WebRadio a partir de /var/opt/mssql/backup/webradio.bak. Só pelo `sa`, no one-shot `restaurar` (RESTORE exige
   CREATE DATABASE/dbcreator, que o svc_backup não tem). Ordem completa do E5-D13, com api, worker e migrator PARADOS:
     db-init (logins) -> restore.sql -> pos-restore.sql -> mc mirror da mídia -> migrator -> api e worker
   Rodar o pos-restore.sql logo depois é obrigatório: os usuários do banco voltam com o SID do servidor antigo e ficam órfãos.

   1. RESTORE VERIFYONLY ... WITH CHECKSUM antes de mexer no banco: arquivo ilegível ou corrompido aborta com o WebRadio intacto.
   2. SINGLE_USER WITH ROLLBACK IMMEDIATE: o REPLACE exige acesso exclusivo ("exclusive access could not be obtained" se sobrar
      conexão). Quem chama (restaurar.sh) já parou api e worker; isto derruba o resto.
   3. RESTORE DATABASE ... WITH CHECKSUM, REPLACE. O banco criado pelo init-db.sql (vazio) é substituído; os arquivos mantêm os
      nomes lógicos WebRadio e WebRadio_log, então não precisa de MOVE.
   4. MULTI_USER de novo, também se o RESTORE falhar (o banco não pode ficar preso em SINGLE_USER).
   O recovery model volta como o do backup (SIMPLE).
   ===================================================================== */

:on error exit
SET NOCOUNT ON;

RESTORE VERIFYONLY FROM DISK = N'/var/opt/mssql/backup/webradio.bak' WITH CHECKSUM;
GO

IF DB_ID(N'WebRadio') IS NOT NULL
    ALTER DATABASE WebRadio SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
GO

BEGIN TRY
    RESTORE DATABASE WebRadio
        FROM DISK = N'/var/opt/mssql/backup/webradio.bak'
        WITH CHECKSUM, REPLACE, STATS = 100;
END TRY
BEGIN CATCH
    BEGIN TRY
        ALTER DATABASE WebRadio SET MULTI_USER;
    END TRY
    BEGIN CATCH
        PRINT 'Não consegui voltar o WebRadio para MULTI_USER (banco possivelmente em RESTORING).';
    END CATCH;
    THROW;
END CATCH
GO

ALTER DATABASE WebRadio SET MULTI_USER;
GO
PRINT 'RESTORE_SQL_OK';
GO
