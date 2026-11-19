/* =====================================================================
   WebRadio — infra/perf/medir-e4-a01a.sql
   Autor: Atlas (DBA) · tarefa E4-A01a (08, E4-D4 e E4-D5) · 2026-11-08

   Mede as consultas da `proxima` (pedido e rotação) sobre o dataset de gerar-dataset-e4.sql. O texto das consultas é o de
   docs/pareceres/e4-a01a-consultas-playout.md (o Forge copia de lá no E4-F08). Plano real (STATISTICS PROFILE) e STATISTICS IO.
   Tudo que escreve roda em transação com ROLLBACK. Sessão com os SET do EF (QUOTED_IDENTIFIER e ARITHABORT ligados).
   ===================================================================== */
SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; SET ARITHABORT ON;
GO
PRINT '=== D0 distribuicao da chave Sorteio (tem de ser uniforme e sem colisao; CHECKSUM(Id) dava 4.086 valores para 40.000 midias) ===';
SELECT COUNT(*) AS midias, COUNT(DISTINCT Sorteio) AS valores_distintos, MIN(Sorteio) AS minimo, MAX(Sorteio) AS maximo FROM midia.ArquivoMidia;
SELECT decil, MIN(Sorteio) AS de, MAX(Sorteio) AS ate, COUNT(*) AS midias
FROM (SELECT Sorteio, NTILE(10) OVER (ORDER BY Sorteio) AS decil FROM midia.ArquivoMidia) AS x GROUP BY decil ORDER BY decil;
;WITH o AS (SELECT CAST(Sorteio AS bigint) AS k FROM midia.ArquivoMidia), g AS (SELECT k - LAG(k) OVER (ORDER BY k) AS gap FROM o)
SELECT MAX(gap) * 1.0 / (4294967296.0 / (SELECT COUNT(*) FROM midia.ArquivoMidia)) AS maior_intervalo_sobre_a_media FROM g;
GO
PRINT '=== P1 pedido (E4-D5): idade 24 h, nunca entregue ou outra instancia, midia Aprovado e Ativo, CTE ordenada ===';
DECLARE @agora datetime2(3) = SYSUTCDATETIME(), @instancia uniqueidentifier = NEWID();
DECLARE @corte datetime2(3) = DATEADD(HOUR, -24, @agora);
DBCC DROPCLEANBUFFERS; DBCC FREEPROCCACHE;
SET STATISTICS IO ON; SET STATISTICS PROFILE ON;
BEGIN TRAN;
;WITH c AS (
    SELECT TOP (1) p.Id, p.MidiaId, p.EnviadoPlayoutEmUtc, p.EnviadoPlayoutInstancia
    FROM interacao.PedidoMusica AS p WITH (UPDLOCK, READPAST, ROWLOCK, INDEX(IX_PedidoMusica_Playout))
    WHERE p.Status = 2 AND p.MidiaId IS NOT NULL AND p.ModeradoEmUtc >= @corte
      AND (p.EnviadoPlayoutEmUtc IS NULL OR p.EnviadoPlayoutInstancia IS NULL OR p.EnviadoPlayoutInstancia <> @instancia)
      AND EXISTS (SELECT 1 FROM midia.ArquivoMidia AS m WHERE m.Id = p.MidiaId AND m.StatusSanitizacao = 4 AND m.Ativo = 1)
    ORDER BY p.ModeradoEmUtc)
UPDATE c SET EnviadoPlayoutEmUtc = @agora, EnviadoPlayoutInstancia = @instancia
OUTPUT inserted.Id AS PedidoId, inserted.MidiaId;
ROLLBACK;
SET STATISTICS PROFILE OFF; SET STATISTICS IO OFF;
GO
PRINT '=== P2 pedido: ninguem elegivel (fila vazia: o caso mais comum, uma chamada por faixa) ===';
DECLARE @agora datetime2(3) = SYSUTCDATETIME(), @instancia uniqueidentifier = NEWID();
DECLARE @corte datetime2(3) = DATEADD(HOUR, -24, @agora);
-- Ninguem elegivel: marca a instancia de todos os candidatos como a corrente, dentro da transacao.
SET STATISTICS IO ON;
BEGIN TRAN;
UPDATE interacao.PedidoMusica SET EnviadoPlayoutEmUtc = @agora, EnviadoPlayoutInstancia = @instancia WHERE Status = 2 AND MidiaId IS NOT NULL;
SET STATISTICS IO OFF;
DBCC DROPCLEANBUFFERS;
SET STATISTICS IO ON;
;WITH c AS (
    SELECT TOP (1) p.Id, p.MidiaId, p.EnviadoPlayoutEmUtc, p.EnviadoPlayoutInstancia
    FROM interacao.PedidoMusica AS p WITH (UPDLOCK, READPAST, ROWLOCK, INDEX(IX_PedidoMusica_Playout))
    WHERE p.Status = 2 AND p.MidiaId IS NOT NULL AND p.ModeradoEmUtc >= @corte
      AND (p.EnviadoPlayoutEmUtc IS NULL OR p.EnviadoPlayoutInstancia IS NULL OR p.EnviadoPlayoutInstancia <> @instancia)
      AND EXISTS (SELECT 1 FROM midia.ArquivoMidia AS m WHERE m.Id = p.MidiaId AND m.StatusSanitizacao = 4 AND m.Ativo = 1)
    ORDER BY p.ModeradoEmUtc)
UPDATE c SET EnviadoPlayoutEmUtc = @agora, EnviadoPlayoutInstancia = @instancia
OUTPUT inserted.Id AS PedidoId, inserted.MidiaId;
SET STATISTICS IO OFF;
ROLLBACK;
GO
PRINT '=== R1 rotacao Musica, busca 1: Sorteio >= @r ===';
DECLARE @tipo tinyint = 3, @r int = CHECKSUM(NEWID()), @corte datetime2(0) = DATEADD(MINUTE, -90, SYSUTCDATETIME()), @emVoo uniqueidentifier = NEWID();
DBCC DROPCLEANBUFFERS; DBCC FREEPROCCACHE;
SET STATISTICS IO ON; SET STATISTICS PROFILE ON;
SELECT TOP (1) a.Id, a.Titulo, a.Artista
FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio >= @r AND a.Id <> @emVoo
  AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo)) WHERE r.IniciadoEmUtc >= @corte AND r.MidiaId = a.Id)
ORDER BY a.Sorteio;
SET STATISTICS PROFILE OFF; SET STATISTICS IO OFF;
GO
PRINT '=== R2 rotacao Musica, busca 2 (volta ao inicio): Sorteio < @r ===';
DECLARE @tipo tinyint = 3, @r int = CHECKSUM(NEWID()), @corte datetime2(0) = DATEADD(MINUTE, -90, SYSUTCDATETIME()), @emVoo uniqueidentifier = NEWID();
SET STATISTICS IO ON;
SELECT TOP (1) a.Id, a.Titulo, a.Artista
FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio < @r AND a.Id <> @emVoo
  AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo)) WHERE r.IniciadoEmUtc >= @corte AND r.MidiaId = a.Id)
ORDER BY a.Sorteio;
SET STATISTICS IO OFF;
GO
PRINT '=== R3 conjunto recente (fallback: a que tocou ha mais tempo dentro da janela) ===';
DECLARE @tipo tinyint = 3, @corte datetime2(0) = DATEADD(MINUTE, -90, SYSUTCDATETIME()), @emVoo uniqueidentifier = NEWID();
SET STATISTICS IO ON; SET STATISTICS PROFILE ON;
SELECT TOP (1) a.Id, a.Titulo, a.Artista
FROM (SELECT r.MidiaId, MAX(r.IniciadoEmUtc) AS Ultima FROM midia.Reproducao AS r WHERE r.IniciadoEmUtc >= @corte GROUP BY r.MidiaId) AS u
JOIN midia.ArquivoMidia AS a ON a.Id = u.MidiaId
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Id <> @emVoo
ORDER BY u.Ultima, a.Id;
SET STATISTICS PROFILE OFF; SET STATISTICS IO OFF;
GO
PRINT '=== R4 ida e volta da rotacao no caso comum: R1 + carga da linha (PK) que o Forge faz para a URL pre-assinada ===';
DECLARE @tipo tinyint = 3, @r int = CHECKSUM(NEWID()), @corte datetime2(0) = DATEADD(MINUTE, -90, SYSUTCDATETIME()), @emVoo uniqueidentifier = NEWID(), @id uniqueidentifier;
SET STATISTICS IO ON;
SELECT TOP (1) @id = a.Id FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio >= @r AND a.Id <> @emVoo
  AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo)) WHERE r.IniciadoEmUtc >= @corte AND r.MidiaId = a.Id) ORDER BY a.Sorteio;
SELECT a.Bucket, a.ChaveStorage, a.Titulo, a.Artista FROM midia.ArquivoMidia AS a WHERE a.Id = @id;
SET STATISTICS IO OFF;
GO
PRINT '=== R5 pior caso: biblioteca pequena (300 comerciais ativos, todos dentro da janela): R1, R2 e R3 em sequencia ===';
BEGIN TRAN;
;WITH x AS (SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) AS n FROM midia.ArquivoMidia WHERE TipoMidia = 2 AND StatusSanitizacao = 4 AND Ativo = 1)
UPDATE a SET Ativo = 0 FROM midia.ArquivoMidia a JOIN x ON x.Id = a.Id WHERE x.n > 300;
INSERT midia.Reproducao (MidiaId, IniciadoEmUtc) SELECT Id, DATEADD(MINUTE, -5, SYSUTCDATETIME()) FROM midia.ArquivoMidia WHERE TipoMidia = 2 AND StatusSanitizacao = 4 AND Ativo = 1;
DECLARE @tipo tinyint = 2, @r int = CHECKSUM(NEWID()), @corte datetime2(0) = DATEADD(MINUTE, -20, SYSUTCDATETIME()), @emVoo uniqueidentifier = NEWID();
SET STATISTICS IO ON;
SELECT TOP (1) a.Id FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio >= @r AND a.Id <> @emVoo
  AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo)) WHERE r.IniciadoEmUtc >= @corte AND r.MidiaId = a.Id) ORDER BY a.Sorteio;
SELECT TOP (1) a.Id FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio < @r AND a.Id <> @emVoo
  AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo)) WHERE r.IniciadoEmUtc >= @corte AND r.MidiaId = a.Id) ORDER BY a.Sorteio;
SELECT TOP (1) a.Id FROM (SELECT r.MidiaId, MAX(r.IniciadoEmUtc) AS Ultima FROM midia.Reproducao AS r WHERE r.IniciadoEmUtc >= @corte GROUP BY r.MidiaId) AS u
JOIN midia.ArquivoMidia AS a ON a.Id = u.MidiaId WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Id <> @emVoo ORDER BY u.Ultima, a.Id;
SET STATISTICS IO OFF;
ROLLBACK;
GO
