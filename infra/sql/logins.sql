/* =====================================================================
   WebRadio — infra/sql/logins.sql
   Autor: Atlas (DBA) · tarefa E1-A02 · 2026-09-27

   Executado pelo "db-init" (D20) logo após infra/sql/init-db.sql, ainda com
   a SA (S-A10) — é o único momento em que a SA cria outros principals.
   Nenhum dos três é sysadmin; cada um só enxerga o que precisa.

   Idempotente por CRIAÇÃO, não por senha: se o login já existe, o CREATE é
   pulado e a senha atual é preservada. Rotação de senha em produção usa
   ALTER LOGIN à parte (fora deste script) — se este script resetasse a
   senha a cada execução, um segredo rotacionado no cofre mas ainda não
   atualizado aqui derrubaria a autenticação de todos os serviços no
   próximo restart do db-init.

   Variáveis sqlcmd (nunca literais — S-A10, docs/specs/05-seguranca-auditoria.md §5.3):
     $(DB_APP_PASSWORD)       senha de webradio_app        (api, worker)
     $(DB_MIGRATOR_PASSWORD)  senha de webradio_migrator   (migrator)
     $(DB_RELATORIO_PASSWORD) senha de webradio_relatorio  (ferramenta de
                               relatório — ainda sem serviço no Épico 1)

   Uso:
     sqlcmd -b -C -S sqlserver -U sa -P "$(cat /run/secrets/mssql_sa_password)" \
       -v DB_APP_PASSWORD="$(cat /run/secrets/db_app_password)" \
          DB_MIGRATOR_PASSWORD="$(cat /run/secrets/db_migrator_password)" \
          DB_RELATORIO_PASSWORD="$(cat /run/secrets/db_relatorio_password)" \
       -i infra/sql/logins.sql
   ===================================================================== */

:on error exit
SET NOCOUNT ON;
USE WebRadio;
GO

/* ---------- 1. webradio_migrator — só o serviço "migrator" (EF Core Migrations) ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'webradio_migrator')
BEGIN
    CREATE LOGIN webradio_migrator WITH PASSWORD = N'$(DB_MIGRATOR_PASSWORD)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'webradio_migrator')
BEGIN
    CREATE USER webradio_migrator FOR LOGIN webradio_migrator;
END
GO
-- DDL para aplicar migrations (inclui a tabela __EFMigrationsHistory), sem CREATE/ALTER
-- LOGIN, sem GRANT e sem acesso a outro banco além de WebRadio.
ALTER ROLE db_ddladmin   ADD MEMBER webradio_migrator;
ALTER ROLE db_datareader ADD MEMBER webradio_migrator;
ALTER ROLE db_datawriter ADD MEMBER webradio_migrator;
GO

/* ---------- 2. webradio_app — api e worker ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'webradio_app')
BEGIN
    CREATE LOGIN webradio_app WITH PASSWORD = N'$(DB_APP_PASSWORD)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'webradio_app')
BEGIN
    CREATE USER webradio_app FOR LOGIN webradio_app;
END
GO
-- Sem db_ddladmin: webradio_app não consegue CREATE/ALTER/DROP TABLE (critério de
-- aceite E1-A02).
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::seg       TO webradio_app;
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::grade     TO webradio_app;
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::interacao TO webradio_app;
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::midia     TO webradio_app;
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::infra     TO webradio_app;
-- sp_getapplock (ADR D7) é um procedimento de sistema, acessível a qualquer principal
-- com acesso ao banco — não precisa de GRANT explícito.
GO

/* ---------- 3. webradio_relatorio — leitura para relatórios (Épico 3) ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'webradio_relatorio')
BEGIN
    CREATE LOGIN webradio_relatorio WITH PASSWORD = N'$(DB_RELATORIO_PASSWORD)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'webradio_relatorio')
BEGIN
    CREATE USER webradio_relatorio FOR LOGIN webradio_relatorio;
END
GO
-- Só leitura, e só nos schemas com dado relevante a relatório. 'seg' fica de fora de
-- propósito: webradio_relatorio nunca precisa ver SenhaHash/Email.
GRANT SELECT ON SCHEMA::midia     TO webradio_relatorio;
GRANT SELECT ON SCHEMA::grade     TO webradio_relatorio;
GRANT SELECT ON SCHEMA::interacao TO webradio_relatorio;
-- [Atlas] EXECUTE ON SCHEMA::rel fica para quando o Épico 3 criar o schema `rel`
-- (rel.usp_MaisTocadas, tarefa A07) — GRANT numa schema inexistente falha agora.
GO

PRINT 'logins.sql concluído.';
GO
