/* =====================================================================
   WebRadio — infra/sql/pos-restore.sql
   Autor: Atlas (DBA) · tarefa E5-A01 (09-epico-5-tarefas.md, E5-D13)

   Religa os usuários do banco aos logins do servidor depois do restore.
   Os LOGINs vivem no master e não vão no .bak. Num servidor novo o db-init recria os logins com SID novo, e os usuários que
   voltam no .bak (webradio_app, webradio_migrator, webradio_relatorio, svc_backup) carregam o SID do servidor antigo: ficam
   ÓRFÃOS e o login falha com "Cannot open database ... requested by the login" (testado no E5-A01). Re-rodar o logins.sql não
   resolve: ele só faz CREATE USER quando o usuário não existe.
   Rodar com o `sa`, no WebRadio, DEPOIS do restore.sql e ANTES do migrator, da api e do worker.

   Para cada usuário SQL órfão do banco (sem login de mesmo SID): se existe um login de mesmo NOME, ALTER USER ... WITH LOGIN
   (idempotente; as permissões e os papéis ficam no banco, vêm do .bak). Se não existe, ABORTA: o db-init não rodou antes.
   No fim exige zero órfãos.
   ===================================================================== */

:on error exit
SET NOCOUNT ON;
USE WebRadio;
GO

DECLARE @nome sysname, @sql nvarchar(400);
DECLARE orfaos CURSOR LOCAL FAST_FORWARD FOR
    SELECT dp.name
    FROM sys.database_principals AS dp
    WHERE dp.type = 'S' AND dp.authentication_type = 1
      AND dp.name NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys')
      AND NOT EXISTS (SELECT 1 FROM sys.server_principals AS sp WHERE sp.sid = dp.sid);
OPEN orfaos;
FETCH NEXT FROM orfaos INTO @nome;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @nome AND type = 'S')
    BEGIN
        RAISERROR(N'pos-restore: o usuário %s não tem login no servidor. Rode o db-init (logins.sql e logins-backup.sql) antes.', 16, 1, @nome);
        RETURN;
    END
    SET @sql = N'ALTER USER ' + QUOTENAME(@nome) + N' WITH LOGIN = ' + QUOTENAME(@nome) + N';';
    EXEC (@sql);
    PRINT N'POS_RESTORE: usuário ' + @nome + N' religado ao login.';
    FETCH NEXT FROM orfaos INTO @nome;
END
CLOSE orfaos;
DEALLOCATE orfaos;

IF EXISTS (
    SELECT 1 FROM sys.database_principals AS dp
    WHERE dp.type = 'S' AND dp.authentication_type = 1
      AND dp.name NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys')
      AND NOT EXISTS (SELECT 1 FROM sys.server_principals AS sp WHERE sp.sid = dp.sid))
BEGIN
    RAISERROR(N'pos-restore: ainda há usuário órfão no WebRadio.', 16, 1);
    RETURN;
END
PRINT N'POS_RESTORE_OK';
GO
