/* =====================================================================
   WebRadio — infra/sql/seed-dev.sql
   Autor: Atlas (DBA) · tarefa E1-A06 · 2026-09-27

   NUNCA roda em produção: guardado por SEED_DEV=true (decisão de quem invoca
   este script) e, dentro dele, por $(Ambiente)=Development (defesa extra).

   Os 2 locutores NÃO são criados aqui: o DevDataSeeder (C#, Forge, E1-F07)
   os cria com SenhaHash no formato do PasswordHasher<Usuario>, e este script
   só os encontra por EmailNormalizado. Só dados fictícios (@example.com) —
   o repositório é público.

   [Atlas/Sentinel/Guardian] Todas as 3 variáveis abaixo são OBRIGATÓRIAS via
   -v, SEM valor padrão neste script. Um :setvar aqui tem precedência MAIOR
   que -v (achado real, reproduzido em SQL Server 2022): a trava de ambiente
   nunca disparava e os e-mails passados por -v eram ignorados. Sem -v
   Ambiente=..., o sqlcmd falha com "variável não definida" antes de rodar
   qualquer coisa — isso já é fail-closed. Os valores de dev (Development,
   locutor1@example.com, locutor2@example.com) ficam no comando do
   db-init/compose, nunca aqui.

   Variáveis sqlcmd (todas obrigatórias):
     $(Ambiente)       'Development' — qualquer outro valor aborta
     $(Locutor1Email)  ex.: 'locutor1@example.com'
     $(Locutor2Email)  ex.: 'locutor2@example.com'

   Uso:
     sqlcmd -b -C -S sqlserver -U sa \
       -v Ambiente="$ASPNETCORE_ENVIRONMENT" \
          Locutor1Email="locutor1@example.com" Locutor2Email="locutor2@example.com" \
       -i infra/sql/seed-dev.sql

   Reseed determinístico, não idempotente por IF NOT EXISTS: cada execução
   apaga só o que este próprio script criou (marcado com o prefixo
   "[seed] " NO TÍTULO **e** um dos dois LocutorId de seed — nunca toca em
   programa/divulgação de outro autor) e recria a grade ancorada em "amanhã"
   (UTC). Isso garante que toda subida do compose mostra uma semana futura de
   verdade, em vez de acumular lixo.

   [Atlas/Guardian, M1] A grade nova só entra nos dias sem interseção
   [Inicio, Fim) — mesma regra do 01-dominio.md §2.3 — com QUALQUER programa
   Agendado/AoVivo já existente, não só os do seed. Um dia em conflito com um
   programa criado por quem está testando a API é pulado, não sobrescrito.

   Tudo roda numa única transação (XACT_ABORT ON): se o INSERT falhar depois
   do DELETE, a transação desfaz tudo — nunca fica com a grade vazia. Usa o
   mesmo sp_getapplock 'grade:programa' do ADR D7 antes de mexer na grade.

   Limitação conhecida: se algum PedidoMusica de teste já referenciar um
   Programa de um lote "[seed] " anterior, o DELETE falha por FK
   (FK_PedidoMusica_Programa não tem CASCADE, de propósito). Em dev isso
   normalmente some com `docker compose down -v`.
   ===================================================================== */

:on error exit

SET NOCOUNT ON;
SET XACT_ABORT ON;
-- [Atlas] QUOTED_IDENTIFIER ON: grade.Programa e interacao.Divulgacao têm índices
-- filtrados (03-schema-sqlserver.sql), e sqlcmd liga essa opção OFF por padrão — sem
-- isso, até um DELETE simples contra essas tabelas falha com o erro 1934.
SET QUOTED_IDENTIFIER ON;
USE WebRadio;
GO

IF (N'$(Ambiente)' <> N'Development')
BEGIN
    RAISERROR(N'seed-dev.sql: só roda com Ambiente=Development (SEED_DEV é só para dev). Abortando.', 16, 1);
    RETURN;
END

DECLARE @Locutor1Id uniqueidentifier, @Locutor2Id uniqueidentifier;
SELECT @Locutor1Id = Id FROM seg.Usuario WHERE EmailNormalizado = UPPER(N'$(Locutor1Email)');
SELECT @Locutor2Id = Id FROM seg.Usuario WHERE EmailNormalizado = UPPER(N'$(Locutor2Email)');

IF @Locutor1Id IS NULL OR @Locutor2Id IS NULL
BEGIN
    RAISERROR(N'seed-dev.sql: locutor(es) fictício(s) não encontrados por EmailNormalizado ($(Locutor1Email) / $(Locutor2Email)). Rode o DevDataSeeder (Forge, E1-F07) antes deste script.', 16, 1);
    RETURN;
END

BEGIN TRAN;

-- [Atlas/Guardian, LOW] Mesma trava do ADR D7 (POST /programas): evita corrida com a
-- API enquanto o seed roda. Baixo risco em dev, mas barato de manter consistente.
DECLARE @LockResult int;
EXEC @LockResult = sp_getapplock @Resource = 'grade:programa', @LockMode = 'Exclusive', @LockOwner = 'Transaction';
IF @LockResult < 0
BEGIN
    ROLLBACK;
    RAISERROR(N'seed-dev.sql: não consegui o lock grade:programa (sp_getapplock retornou %d). Abortando.', 16, 1, @LockResult);
    RETURN;
END

-- ---------- Grade: 7 dias, sem sobreposição com QUALQUER programa Agendado/AoVivo existente ----------
-- Reseed remove só o que ESTE script criou: prefixo "[seed] " no título E LocutorId é
-- um dos dois locutores fictícios — nunca toca em programa de outro autor.
DELETE FROM grade.Programa
WHERE LEFT(Titulo, 7) = N'[seed] '
  AND LocutorId IN (@Locutor1Id, @Locutor2Id);

DECLARE @Base datetime2(0) = DATEADD(DAY, 1, CAST(CAST(SYSUTCDATETIME() AS date) AS datetime2(0)));
DECLARE @Candidatos TABLE (n int PRIMARY KEY, InicioUtc datetime2(0), FimUtc datetime2(0));
INSERT INTO @Candidatos (n, InicioUtc, FimUtc)
SELECT n, DATEADD(HOUR, 12, DATEADD(DAY, n, @Base)), DATEADD(HOUR, 14, DATEADD(DAY, n, @Base))
FROM (VALUES (0), (1), (2), (3), (4), (5), (6)) AS Dias(n);

-- Só entram os dias sem interseção com programa Agendado(1)/AoVivo(2) já existente —
-- inclui programas de qualquer autor, não só os do seed (M1).
INSERT INTO grade.Programa (Titulo, Descricao, LocutorId, InicioUtc, FimUtc, Status)
SELECT
    N'[seed] Programa Dia ' + CAST(c.n + 1 AS nvarchar(1)),
    N'Programa de exemplo gerado por infra/sql/seed-dev.sql — dados fictícios.',
    CASE WHEN c.n % 2 = 0 THEN @Locutor1Id ELSE @Locutor2Id END,
    c.InicioUtc,
    c.FimUtc,
    1 -- Agendado
FROM @Candidatos c
WHERE NOT EXISTS (
    SELECT 1 FROM grade.Programa p
    WHERE p.Status IN (1, 2)
      AND p.InicioUtc < c.FimUtc
      AND p.FimUtc > c.InicioUtc
);

DECLARE @Inseridos int = @@ROWCOUNT;
IF @Inseridos < 7
    PRINT N'seed-dev.sql: ' + CAST(7 - @Inseridos AS nvarchar(1)) + N' dia(s) da grade pulado(s) por conflito com programa existente.';

-- ---------- 3 divulgações ----------
DELETE FROM interacao.Divulgacao
WHERE LEFT(Titulo, 7) = N'[seed] '
  AND CriadoPorUsuarioId IN (@Locutor1Id, @Locutor2Id);

INSERT INTO interacao.Divulgacao (Titulo, Mensagem, Ativo, Prioridade, CriadoPorUsuarioId)
VALUES
    (N'[seed] Bem-vindo à WebRadio', N'Ambiente de desenvolvimento — dados fictícios, sem relação com pessoas reais.', 1, 80, @Locutor1Id),
    (N'[seed] Peça sua música', N'Peça sua música favorita direto pelo site, sem precisar de cadastro.', 1, 50, @Locutor1Id),
    (N'[seed] Programação da semana', N'Confira a grade da semana na aba Grade.', 1, 30, @Locutor2Id);

COMMIT;

PRINT N'seed-dev.sql concluído: ' + CAST(@Inseridos AS nvarchar(1)) + N' dia(s) de grade + 3 divulgações.';
GO
