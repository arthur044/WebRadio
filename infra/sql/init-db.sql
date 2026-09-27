/* =====================================================================
   WebRadio — infra/sql/init-db.sql
   Autor: Atlas (DBA) · tarefa E1-A04 · 2026-09-27

   Executado pelo serviço one-shot "db-init" (ADR D20, docs/specs/00-arquitetura.md),
   autenticado como SA lida de /run/secrets — o ÚNICO passo do stack que usa a SA
   (S-A10, docs/specs/05-seguranca-auditoria.md). Depois dele roda o logins.sql
   (E1-A02), e só então o "migrator" (webradio_migrator, sem SA) aplica as
   EF Core Migrations.

   Idempotente por construção: cada ALTER só roda se o estado atual divergir do
   desejado. Isso importa porque READ_COMMITTED_SNAPSHOT usa WITH ROLLBACK IMMEDIATE
   (derruba transações abertas) — sem o guard, toda subida do compose tentaria a
   exclusividade de banco à toa mesmo já estando correto.

   Variável sqlcmd:
     $(Ambiente)  'Development' (padrão neste script) ou 'Production' — mesmos
                  valores de ASPNETCORE_ENVIRONMENT (docs/specs/05-seguranca-auditoria.md
                  §5.1) — decide o recovery model (E1-A04).

   Uso (db-init, senha da SA em arquivo, nunca no docker-compose.yml — S-A10):
     sqlcmd -b -C -S sqlserver -U sa -P "$(cat /run/secrets/mssql_sa_password)" \
       -v Ambiente="$ASPNETCORE_ENVIRONMENT" -i infra/sql/init-db.sql
   ===================================================================== */

:setvar Ambiente "Development"
:on error exit

SET NOCOUNT ON;

/* ---------- 1. Banco: collation fixa (03-schema-sqlserver.sql), cria só se não existir ---------- */
IF DB_ID(N'WebRadio') IS NULL
BEGIN
    PRINT 'Criando banco WebRadio...';
    -- Collation acento-insensível para busca de título/artista ("Canção" = "cancao").
    -- Colunas que precisam de comparação exata (e-mail normalizado, chave de objeto
    -- no storage) têm COLLATE próprio no 03-schema-sqlserver.sql — não dependem desta.
    CREATE DATABASE WebRadio COLLATE Latin1_General_100_CI_AI_SC;
END
ELSE
BEGIN
    PRINT 'Banco WebRadio já existe, pulando CREATE DATABASE.';
END
GO

/* ---------- 2. Opções de banco (cada ALTER só roda se o estado atual divergir) ---------- */

-- RCSI: leitores não bloqueiam escritores. É por isso que a regra de não
-- sobreposição da grade usa sp_getapplock (ADR D7) em vez de trigger.
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'WebRadio' AND is_read_committed_snapshot_on = 1)
BEGIN
    PRINT 'Ligando READ_COMMITTED_SNAPSHOT em WebRadio...';
    ALTER DATABASE WebRadio SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'WebRadio' AND snapshot_isolation_state = 1)
BEGIN
    PRINT 'Ligando ALLOW_SNAPSHOT_ISOLATION em WebRadio...';
    ALTER DATABASE WebRadio SET ALLOW_SNAPSHOT_ISOLATION ON;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'WebRadio' AND compatibility_level = 160)
BEGIN
    PRINT 'Ajustando COMPATIBILITY_LEVEL de WebRadio para 160...';
    ALTER DATABASE WebRadio SET COMPATIBILITY_LEVEL = 160;
END
GO

-- Recovery model por ambiente (E1-A04): SIMPLE em dev/CI (sem estratégia de
-- backup no Épico 1; log não precisa crescer) e FULL em produção (prepara
-- point-in-time recovery para quando a estratégia de backup entrar).
IF (N'$(Ambiente)' = N'Production')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'WebRadio' AND recovery_model_desc = 'FULL')
    BEGIN
        PRINT 'Ambiente Production: ajustando recovery model de WebRadio para FULL...';
        ALTER DATABASE WebRadio SET RECOVERY FULL;
    END
END
ELSE
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'WebRadio' AND recovery_model_desc = 'SIMPLE')
    BEGIN
        PRINT 'Ambiente ' + N'$(Ambiente)' + ': ajustando recovery model de WebRadio para SIMPLE...';
        ALTER DATABASE WebRadio SET RECOVERY SIMPLE;
    END
END
GO

PRINT 'init-db.sql concluído.';
GO
