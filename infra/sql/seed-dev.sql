/* =====================================================================
   WebRadio — infra/sql/seed-dev.sql
   Autor: Atlas (DBA) · tarefa E1-A06 · 2026-09-27

   NUNCA roda em produção: guardado por SEED_DEV=true (decisão de quem invoca
   este script) e, dentro dele, por $(Ambiente)=Development (defesa extra —
   ver a checagem logo abaixo).

   Os 2 locutores NÃO são criados aqui: o DevDataSeeder (C#, Forge, E1-F07)
   os cria com SenhaHash no formato do PasswordHasher<Usuario>, e este script
   só os encontra por EmailNormalizado. Só dados fictícios (@example.com) —
   o repositório é público.

   Reseed determinístico, não idempotente por IF NOT EXISTS: cada execução
   apaga o lote anterior marcado "(seed)" e recria a grade ancorada em
   "amanhã" (UTC). Isso garante que toda subida do compose mostra uma semana
   futura de verdade (útil para o Prism testar a página Grade), em vez de
   acumular lixo ou de a grade "envelhecer" e sumir da view de semana atual.

   Variáveis sqlcmd:
     $(Ambiente)       'Development' (padrão) — abortado se != Development
     $(Locutor1Email)  'locutor1@example.com' (padrão)
     $(Locutor2Email)  'locutor2@example.com' (padrão)

   Limitação conhecida: se algum PedidoMusica de teste já referenciar um
   Programa de um lote "(seed)" anterior, o DELETE falha por FK
   (FK_PedidoMusica_Programa não tem CASCADE, de propósito — ver 03-schema-
   sqlserver.sql). Em dev isso normalmente some com `docker compose down -v`.
   ===================================================================== */

:setvar Ambiente "Development"
:setvar Locutor1Email "locutor1@example.com"
:setvar Locutor2Email "locutor2@example.com"
:on error exit

SET NOCOUNT ON;
-- [Atlas] QUOTED_IDENTIFIER ON: grade.Programa e interacao.Divulgacao têm índices
-- filtrados (03-schema-sqlserver.sql), e sqlcmd liga essa opção OFF por padrão — sem
-- isso, até um DELETE simples contra essas tabelas falha com o erro 1934 (achado
-- rodando de verdade, mesma causa do E1-A01).
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

-- ---------- Grade: 7 dias, sem sobreposição (dias distintos, 2h cada) ----------
DELETE FROM grade.Programa WHERE Titulo LIKE N'Programa Dia % (seed)';

DECLARE @Base datetime2(0) = DATEADD(DAY, 1, CAST(CAST(SYSUTCDATETIME() AS date) AS datetime2(0)));
;WITH Dias(n) AS (
    SELECT 0 UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3
    UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6
)
INSERT INTO grade.Programa (Titulo, Descricao, LocutorId, InicioUtc, FimUtc, Status)
SELECT
    N'Programa Dia ' + CAST(n + 1 AS nvarchar(1)) + N' (seed)',
    N'Programa de exemplo gerado por infra/sql/seed-dev.sql — dados fictícios.',
    CASE WHEN n % 2 = 0 THEN @Locutor1Id ELSE @Locutor2Id END,
    DATEADD(HOUR, 12, DATEADD(DAY, n, @Base)),
    DATEADD(HOUR, 14, DATEADD(DAY, n, @Base)),
    1 -- Agendado
FROM Dias;

-- ---------- 3 divulgações ----------
DELETE FROM interacao.Divulgacao WHERE Titulo IN (
    N'Bem-vindo à WebRadio (seed)', N'Peça sua música (seed)', N'Programação da semana (seed)'
);
INSERT INTO interacao.Divulgacao (Titulo, Mensagem, Ativo, Prioridade, CriadoPorUsuarioId)
VALUES
    (N'Bem-vindo à WebRadio (seed)', N'Ambiente de desenvolvimento — dados fictícios, sem relação com pessoas reais.', 1, 80, @Locutor1Id),
    (N'Peça sua música (seed)', N'Peça sua música favorita direto pelo site, sem precisar de cadastro.', 1, 50, @Locutor1Id),
    (N'Programação da semana (seed)', N'Confira a grade da semana na aba Grade.', 1, 30, @Locutor2Id);

PRINT N'seed-dev.sql concluído: 7 dias de grade + 3 divulgações.';
GO
