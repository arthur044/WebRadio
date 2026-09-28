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

   [Atlas/Sentinel/Guardian] Contrato de alfabeto das senhas (Forge, E1-F18 —
   gerar-segredos.sh/.ps1, docs/specs/04-epico-1-tarefas.md): só
   [A-Za-z0-9_-], nunca aspa simples. `WITH PASSWORD = N'$(DB_APP_PASSWORD)'`
   entra por substituição de TEXTO do sqlcmd, não por parâmetro real — um
   valor com ' quebra a string N'...' e o texto depois dela passa a ser SQL
   executado como SA. Um IF/CHARINDEX aqui DENTRO do script não pega isso:
   a substituição já corrompeu a sintaxe da linha antes de qualquer T-SQL
   rodar (o mesmo ' que quebraria o CREATE LOGIN quebra igualmente a
   checagem que tentasse testá-lo primeiro). A validação de verdade tem que
   rodar ANTES do sqlcmd, no shell que lê o segredo — ex., no entrypoint do
   db-init:
     for v in "$DB_APP_PASSWORD" "$DB_MIGRATOR_PASSWORD" "$DB_RELATORIO_PASSWORD"; do
       case "$v" in *[!A-Za-z0-9_-]*) echo "senha fora do alfabeto seguro"; exit 1;; esac
     done
   O gerar-segredos (E1-F18) já não gera fora desse alfabeto; isso é a
   defesa de segundo nível caso alguém troque uma senha à mão.

   Uso (senha da SA via SQLCMDPASSWORD — nunca em -P, que fica visível em `ps`/
   `docker inspect`; as três variáveis de -v abaixo são script vars do sqlcmd,
   não a senha de conexão, e não têm equivalente por env var nativo — o
   contêiner db-init precisa continuar efêmero e sem outro processo rodando):
     export SQLCMDPASSWORD="$(cat /run/secrets/mssql_sa_password)"
     sqlcmd -b -C -S sqlserver -U sa \
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
-- [Sentinel, nota aceita] db_ddladmin alcança objetos em dbo dentro do banco (ex.:
-- procedure com EXECUTE AS OWNER). Risco aceito: migrator é one-shot e o segredo dele
-- só vai para esse serviço (S-A10).
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
-- [Sentinel, item futuro — não bloqueia] midia.Reproducao é só-inserção, mas o GRANT
-- por schema dá UPDATE/DELETE nela também. A tabela não existe ainda quando este script
-- roda, então um DENY UPDATE, DELETE ON midia.Reproducao TO webradio_app fica para uma
-- migration depois que ela existir (reforça a imutabilidade do histórico de playout).
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
-- [Atlas/Sentinel] Só leitura, e só nos schemas sem PII/LGPD. 'seg' fica de fora de
-- propósito (SenhaHash/Email); 'interacao' também: PedidoMusica.NomeOuvinte e
-- Mensagem são dados de ouvinte livremente digitados, sem anonimização — um
-- relatório de "mais tocadas" não precisa ler pedidos, só midia/grade. Sem
-- db_datareader/db_owner: GRANT explícito por schema, nada além disso.
GRANT SELECT ON SCHEMA::midia TO webradio_relatorio;
GRANT SELECT ON SCHEMA::grade TO webradio_relatorio;
-- [Atlas] EXECUTE ON SCHEMA::rel fica para quando o Épico 3 criar o schema `rel`
-- (rel.usp_MaisTocadas, tarefa A07) — GRANT numa schema inexistente falha agora.
GO

PRINT 'logins.sql concluído.';
GO
