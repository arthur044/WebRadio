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
   No fim exige zero órfãos e CONFERE o que o .bak trouxe (Sentinel no #167): como o .bak é só dado, um backup feito depois de uma invasão
   pode trazer papel plantado (db_owner no webradio_app, usuário a mais, guest com CONNECT). Aborta, nomeando o que achou, se:
     - db_owner, db_securityadmin ou db_accessadmin tem membro além do dbo;
     - os papéis dos 4 usuários não são EXATAMENTE os do logins.sql e do logins-backup.sql (migrator: db_ddladmin, db_datareader,
       db_datawriter; svc_backup: db_backupoperator; webradio_app e webradio_relatorio: nenhum papel fixo, só GRANT por schema);
     - existe usuário do banco além dos 4 (dbo, guest, INFORMATION_SCHEMA e sys à parte);
     - o guest tem CONNECT;
     - os 4 usuários têm permissão direta perigosa (CONTROL, TAKE OWNERSHIP, IMPERSONATE, ALTER, CREATE ou WITH GRANT OPTION);
     - um schema do WebRadio tem dono diferente do dbo;
     - há application role no banco (entra no "usuário a mais").
   As conferências rodam ANTES de religar qualquer usuário: num .bak plantado o script aborta com todos os logins ainda órfãos (nenhum
   abre o banco). Lista negada, não lista exata de GRANT: GRANT legítimo novo no logins.sql não derruba o restore. Se mudar os papéis
   do logins.sql, mude a lista esperada aqui.
   ===================================================================== */

:on error exit
SET NOCOUNT ON;
USE WebRadio;
GO

-- Conferência do que voltou no .bak (ver o cabeçalho). ANTES de religar qualquer usuário: se o .bak está plantado, nenhum login abre o banco (falha fechada).
DECLARE @achado nvarchar(max);
DECLARE @esperado TABLE (usuario sysname, papel sysname);
DECLARE @atual    TABLE (usuario sysname, papel sysname);
DECLARE @nossos   TABLE (usuario sysname);
INSERT @nossos VALUES (N'webradio_app'), (N'webradio_migrator'), (N'webradio_relatorio'), (N'svc_backup');
INSERT @esperado VALUES
    (N'webradio_migrator', N'db_ddladmin'), (N'webradio_migrator', N'db_datareader'), (N'webradio_migrator', N'db_datawriter'),
    (N'svc_backup', N'db_backupoperator');

SELECT @achado = STRING_AGG(CONVERT(nvarchar(max), m.name + N' em ' + r.name), N', ')
FROM sys.database_role_members AS rm
JOIN sys.database_principals AS r ON r.principal_id = rm.role_principal_id
JOIN sys.database_principals AS m ON m.principal_id = rm.member_principal_id
WHERE r.name IN (N'db_owner', N'db_securityadmin', N'db_accessadmin') AND m.name <> N'dbo';
IF @achado IS NOT NULL
BEGIN
    RAISERROR(N'pos-restore: papel de administração com membro inesperado no .bak: %s', 16, 1, @achado);
    RETURN;
END

INSERT @atual
SELECT m.name, r.name
FROM sys.database_role_members AS rm
JOIN sys.database_principals AS r ON r.principal_id = rm.role_principal_id
JOIN sys.database_principals AS m ON m.principal_id = rm.member_principal_id
WHERE m.name IN (SELECT usuario FROM @nossos);
SELECT @achado = STRING_AGG(CONVERT(nvarchar(max), d.usuario + N':' + d.papel + N' (' + d.tipo + N')'), N', ')
FROM (
    SELECT usuario, papel, N'a mais' AS tipo FROM (SELECT usuario, papel FROM @atual EXCEPT SELECT usuario, papel FROM @esperado) AS x
    UNION ALL
    SELECT usuario, papel, N'faltando' FROM (SELECT usuario, papel FROM @esperado EXCEPT SELECT usuario, papel FROM @atual) AS y
) AS d;
IF @achado IS NOT NULL
BEGIN
    RAISERROR(N'pos-restore: papéis dos usuários do WebRadio diferentes do esperado: %s', 16, 1, @achado);
    RETURN;
END

SELECT @achado = STRING_AGG(CONVERT(nvarchar(max), name), N', ')
FROM sys.database_principals
WHERE type IN ('S', 'U', 'G', 'E', 'X', 'C', 'K', 'A')
  AND name NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys')
  AND name NOT IN (SELECT usuario FROM @nossos);
IF @achado IS NOT NULL
BEGIN
    RAISERROR(N'pos-restore: usuário a mais no banco restaurado: %s', 16, 1, @achado);
    RETURN;
END

-- Permissão direta perigosa nos 4 usuários (os GRANT legítimos são SELECT/INSERT/UPDATE/DELETE/EXECUTE em schema e CONNECT; os papéis
-- acima cobrem o resto): CONTROL, TAKE OWNERSHIP, IMPERSONATE, ALTER (qualquer), CREATE (qualquer) ou qualquer GRANT ... WITH GRANT OPTION.
SELECT @achado = STRING_AGG(CONVERT(nvarchar(max), u.name + N':' + p.permission_name COLLATE DATABASE_DEFAULT), N', ')
FROM sys.database_permissions AS p
JOIN sys.database_principals AS u ON u.principal_id = p.grantee_principal_id
WHERE u.name IN (SELECT usuario FROM @nossos) AND p.state IN ('G', 'W')
  AND (p.state = 'W' OR p.permission_name IN (N'CONTROL', N'TAKE OWNERSHIP', N'IMPERSONATE', N'ALTER') OR p.permission_name LIKE N'ALTER %' OR p.permission_name LIKE N'CREATE %');
IF @achado IS NOT NULL
BEGIN
    RAISERROR(N'pos-restore: permissão direta perigosa nos usuários do WebRadio: %s', 16, 1, @achado);
    RETURN;
END

-- Dono dos schemas do WebRadio é o dbo (um ALTER AUTHORIZATION plantado daria posse do schema ao usuário).
SELECT @achado = STRING_AGG(CONVERT(nvarchar(max), s.name + N' (dono ' + o.name COLLATE DATABASE_DEFAULT + N')'), N', ')
FROM sys.schemas AS s
JOIN sys.database_principals AS o ON o.principal_id = s.principal_id
WHERE s.name NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys') AND s.name NOT LIKE N'db[_]%' AND s.principal_id <> 1;
IF @achado IS NOT NULL
BEGIN
    RAISERROR(N'pos-restore: schema com dono diferente do dbo no banco restaurado: %s', 16, 1, @achado);
    RETURN;
END

IF EXISTS (SELECT 1 FROM sys.database_permissions
           WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID(N'guest') AND permission_name = N'CONNECT' AND state IN ('G', 'W'))
BEGIN
    RAISERROR(N'pos-restore: o guest tem CONNECT no banco restaurado.', 16, 1);
    RETURN;
END
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
