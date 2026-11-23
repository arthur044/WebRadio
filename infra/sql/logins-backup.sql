/* =====================================================================
   WebRadio — infra/sql/logins-backup.sql
   Autor: Atlas (DBA) · tarefa E5-A01 (09-epico-5-tarefas.md, E5-D3) · 2026-11-23

   Login do backup noturno. Roda no "db-init" (SA) depois do logins.sql, só quando o segredo sql_backup_senha existe
   (infra/db-init/entrypoint.sh). É um arquivo à parte porque o sqlcmd trata variável de ambiente vazia como não definida e aborta:
   o logins.sql não pode ter uma variável opcional.

   svc_backup: db_backupoperator e NADA mais.
     - Pode: BACKUP DATABASE (e BACKUP LOG) do WebRadio, com WITH CHECKSUM.
     - NÃO pode: ler tabela (db_backupoperator não dá SELECT), RESTORE nem RESTORE VERIFYONLY (exigem CREATE DATABASE, que este
       login não tem de propósito: testado no E5-A01), criar banco, nem tocar em outro banco. O login não vai para nenhum
       servidor-role.
     - Aceito: o BACKUP escreve em qualquer caminho em que o processo do SQL Server escreva. A senha só existe no serviço `backup`
       e o caminho do script é fixo (backup.sql).
     - WITH COMPRESSION não funciona na edição Express (padrão do compose): o backup.sql não usa; o restic já comprime.

   Variável (por AMBIENTE, nunca por -v, mesmo contrato do logins.sql; alfabeto [A-Za-z0-9_-] validado no entrypoint):
     $(DB_BACKUP_PASSWORD)    senha de svc_backup
   Idempotente por criação (a senha existente é preservada), como o logins.sql.
   ===================================================================== */

:on error exit
SET NOCOUNT ON;
USE WebRadio;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'svc_backup')
BEGIN
    CREATE LOGIN svc_backup WITH PASSWORD = N'$(DB_BACKUP_PASSWORD)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'svc_backup')
BEGIN
    CREATE USER svc_backup FOR LOGIN svc_backup;
END
GO
ALTER ROLE db_backupoperator ADD MEMBER svc_backup;
GO

PRINT 'logins-backup.sql concluído.';
GO
