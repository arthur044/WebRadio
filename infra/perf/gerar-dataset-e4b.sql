/* =====================================================================
   WebRadio — infra/perf/gerar-dataset-e4b.sql
   Autor: Atlas (DBA) · tarefa E4-A01b (08) · 2026-11-23

   Complemento do gerar-dataset-e4.sql (rodar DEPOIS dele: 50.000 mídias, 2 milhões de Reproducao, 105.000 pedidos). Dá ao dataset o que o
   A01b mede e o A01a não precisava:
     - seg.Usuario: 1 Admin com milhares de envios (A1: 10.000 mídias, 15 delas na última hora), 40 locutores com ~1.000 cada (o C1 é um deles)
       e um locutor no limite de abertos (B1: 3 AguardandoUpload válidos, 1 vencido, 1 Pendente, 1 EmAnalise);
     - DataUploadUtc espalhada em 12 meses (no A01a todas as linhas tinham o mesmo instante) e o status Rejeitado (6) nos 2% que o
       gerar-dataset-e4.sql marcou como 5;
     - 30 mídias Pendente (a fila da sanitização no dia a dia).
   Os números aleatórios são materializados antes do UPDATE (#a), como no gerar-dataset-e4.sql. As FKs continuam em NOCHECK.
   ===================================================================== */
SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON;

-- Usuários: ids fixos para os três de que o medir-e4-a01b.sql precisa; os outros 38 locutores com NEWID().
CREATE TABLE #u (idx int NOT NULL PRIMARY KEY, Id uniqueidentifier NOT NULL);
INSERT #u (idx, Id) VALUES (1, '00000000-0000-0000-0000-0000000000C1');
INSERT #u (idx, Id) SELECT TOP (39) 1 + ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), NEWID() FROM sys.all_columns;
INSERT seg.Usuario (Id, Nome, Email, EmailNormalizado, SenhaHash, Role)
SELECT '00000000-0000-0000-0000-0000000000A1', N'Admin da Rádio', N'admin@perf.test', N'ADMIN@PERF.TEST', N'x', 1
UNION ALL SELECT '00000000-0000-0000-0000-0000000000B1', N'Locutor no limite', N'b1@perf.test', N'B1@PERF.TEST', N'x', 2
UNION ALL SELECT Id, CONCAT(N'Locutor ', idx), CONCAT(N'loc', idx, N'@perf.test'), CONCAT(N'LOC', idx, N'@PERF.TEST'), N'x', 2 FROM #u;

-- Dono, data de envio e (para a quinta parte) o Admin: materializados.
SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) AS rn INTO #r FROM midia.ArquivoMidia;
SELECT Id, CASE WHEN rn % 5 = 0 THEN 0 ELSE 1 + ABS(CHECKSUM(NEWID())) % 40 END AS idx, ABS(CHECKSUM(NEWID())) % 31536000 AS seg INTO #a FROM #r;
CREATE UNIQUE CLUSTERED INDEX ix ON #a (Id);
DECLARE @agora datetime2(3) = SYSUTCDATETIME();
UPDATE m SET EnviadoPorUsuarioId = CASE WHEN a.idx = 0 THEN CAST('00000000-0000-0000-0000-0000000000A1' AS uniqueidentifier) ELSE u.Id END,
             DataUploadUtc = DATEADD(SECOND, -a.seg, @agora),
             UploadExpiraEmUtc = DATEADD(HOUR, 2, DATEADD(SECOND, -a.seg, @agora))
FROM midia.ArquivoMidia AS m JOIN #a AS a ON a.Id = m.Id LEFT JOIN #u AS u ON u.idx = a.idx;

-- Os 2% do gerar-dataset-e4.sql (status 5, no bucket quarentena) são Rejeitado (6) no A01b: 2% rejeitados, como o 08 pede.
UPDATE midia.ArquivoMidia SET StatusSanitizacao = 6 WHERE StatusSanitizacao = 5;

-- O Admin com 15 envios na última hora (abaixo do limite de 20/h: a consulta precisa ler a hora inteira, não parar no 20º).
;WITH x AS (SELECT TOP (15) Id, ROW_NUMBER() OVER (ORDER BY Id) AS n FROM midia.ArquivoMidia WHERE EnviadoPorUsuarioId = '00000000-0000-0000-0000-0000000000A1' ORDER BY Id)
UPDATE m SET DataUploadUtc = DATEADD(MINUTE, -x.n * 3, @agora) FROM midia.ArquivoMidia AS m JOIN x ON x.Id = m.Id;

-- 30 mídias Pendente (a fila do Worker), das últimas 24 h.
;WITH x AS (SELECT TOP (30) Id, ROW_NUMBER() OVER (ORDER BY Id) AS n FROM midia.ArquivoMidia WHERE StatusSanitizacao = 4 AND EnviadoPorUsuarioId <> '00000000-0000-0000-0000-0000000000A1' ORDER BY Id DESC)
UPDATE m SET StatusSanitizacao = 2, Bucket = 'quarentena', MimeType = NULL, DuracaoSegundos = NULL, HashSHA256 = NULL, SanitizadoEmUtc = NULL,
             DataUploadUtc = DATEADD(MINUTE, -x.n * 40, @agora)
FROM midia.ArquivoMidia AS m JOIN x ON x.Id = m.Id;

-- O locutor B1, no limite de 5 abertos: 3 AguardandoUpload válidos, 1 vencido, 1 Pendente, 1 EmAnalise (o vencido não conta).
INSERT midia.ArquivoMidia (NomeOriginal, Titulo, Artista, TipoMidia, Bucket, ChaveStorage, MimeTypeDeclarado, TamanhoBytes, StatusSanitizacao,
    EmAnaliseDesdeUtc, EnviadoPorUsuarioId, DataUploadUtc, UploadExpiraEmUtc, Ativo)
VALUES (N'b1-1.mp3', N'b1 1', N'b1', 3, 'quarentena', 'b1/1.mp3', 'audio/mpeg', 5000000, 1, NULL, '00000000-0000-0000-0000-0000000000B1', DATEADD(MINUTE, -5, @agora),  DATEADD(HOUR, 2, @agora), 1),
       (N'b1-2.mp3', N'b1 2', N'b1', 3, 'quarentena', 'b1/2.mp3', 'audio/mpeg', 5000000, 1, NULL, '00000000-0000-0000-0000-0000000000B1', DATEADD(MINUTE, -4, @agora),  DATEADD(HOUR, 2, @agora), 1),
       (N'b1-3.mp3', N'b1 3', N'b1', 3, 'quarentena', 'b1/3.mp3', 'audio/mpeg', 5000000, 1, NULL, '00000000-0000-0000-0000-0000000000B1', DATEADD(MINUTE, -3, @agora),  DATEADD(HOUR, 2, @agora), 1),
       (N'b1-4.mp3', N'b1 4', N'b1', 3, 'quarentena', 'b1/4.mp3', 'audio/mpeg', 5000000, 1, NULL, '00000000-0000-0000-0000-0000000000B1', DATEADD(HOUR, -5, @agora),  DATEADD(HOUR, -1, @agora), 1),
       (N'b1-5.mp3', N'b1 5', N'b1', 3, 'quarentena', 'b1/5.mp3', 'audio/mpeg', 5000000, 2, NULL, '00000000-0000-0000-0000-0000000000B1', DATEADD(MINUTE, -2, @agora),  DATEADD(HOUR, 2, @agora), 1),
       (N'b1-6.mp3', N'b1 6', N'b1', 3, 'quarentena', 'b1/6.mp3', 'audio/mpeg', 5000000, 3, @agora, '00000000-0000-0000-0000-0000000000B1', DATEADD(MINUTE, -1, @agora), DATEADD(HOUR, 2, @agora), 1);

UPDATE STATISTICS midia.ArquivoMidia WITH FULLSCAN; UPDATE STATISTICS seg.Usuario WITH FULLSCAN;
SELECT u.Nome, COUNT(*) AS midias FROM midia.ArquivoMidia AS m JOIN seg.Usuario AS u ON u.Id = m.EnviadoPorUsuarioId
WHERE u.Id IN ('00000000-0000-0000-0000-0000000000A1', '00000000-0000-0000-0000-0000000000B1', '00000000-0000-0000-0000-0000000000C1') GROUP BY u.Nome;
SELECT StatusSanitizacao, COUNT(*) AS midias FROM midia.ArquivoMidia GROUP BY StatusSanitizacao ORDER BY 1;
