/* =====================================================================
   WebRadio — infra/perf/gerar-dataset-e4.sql
   Autor: Atlas (DBA) · tarefa E4-A01a (08) · 2026-11-08

   Dataset sintético do Épico 4 (schema do 03 já aplicado, banco descartável): 50.000 mídias (80% Musica, 15% Vinheta, 5% Comercial,
   5% desativadas, 2% Rejeitado), 2.000.000 de Reproducao em 12 meses (≈ 228 por hora; 379 linhas numa janela de 90 min) e 105.000 pedidos
   (100.000 Tocado com mídia, 5.000 Aprovado manual sem mídia, 4 Aprovado com mídia em situações distintas, para a `proxima` do E4-D5).
   Os números aleatórios são MATERIALIZADOS antes do INSERT (#k): com CHECKSUM(NEWID()) dentro do JOIN o plano reavalia a função por linha
   e a carga de 2 milhões não termina. As FKs ficam em NOCHECK (não há seg.Usuario): serve a medir leituras, não a testar integridade.
   ===================================================================== */
SET NOCOUNT ON;
ALTER TABLE midia.ArquivoMidia NOCHECK CONSTRAINT FK_ArquivoMidia_EnviadoPor;
ALTER TABLE interacao.PedidoMusica NOCHECK CONSTRAINT ALL;
ALTER TABLE midia.Reproducao NOCHECK CONSTRAINT ALL;
DECLARE @u uniqueidentifier = NEWID();
;WITH n AS (SELECT TOP (50000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_columns a CROSS JOIN sys.all_columns b)
INSERT midia.ArquivoMidia (NomeOriginal, Titulo, Artista, TipoMidia, Bucket, ChaveStorage, MimeTypeDeclarado, MimeType, TamanhoBytes,
    HashSHA256, DuracaoSegundos, StatusSanitizacao, EnviadoPorUsuarioId, UploadExpiraEmUtc, SanitizadoEmUtc, Ativo)
SELECT CONCAT(N'arq', i, N'.mp3'), CONCAT(N'Titulo da faixa numero ', i, N' ao vivo'), CONCAT(N'Artista numero ', i % 3000),
    CASE WHEN i % 100 < 80 THEN 3 WHEN i % 100 < 95 THEN 1 ELSE 2 END,
    'midia', CONCAT('2026/01/', i, '.mp3'), 'audio/mpeg', 'audio/mpeg', 5000000,
    CASE WHEN i % 50 = 0 THEN NULL ELSE CAST(HASHBYTES('SHA2_256', CAST(i AS varchar(12))) AS binary(32)) END,
    200, CASE WHEN i % 50 = 0 THEN 5 ELSE 4 END, @u, SYSUTCDATETIME(), SYSUTCDATETIME(),
    CASE WHEN i % 20 = 0 THEN 0 ELSE 1 END
FROM n;
UPDATE midia.ArquivoMidia SET Bucket = 'quarentena', MimeType = NULL, DuracaoSegundos = NULL, SanitizadoEmUtc = NULL, MotivoRejeicao = N'x' WHERE StatusSanitizacao = 5;

SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) AS rn INTO #m FROM midia.ArquivoMidia WHERE StatusSanitizacao = 4;
CREATE UNIQUE CLUSTERED INDEX ix ON #m (rn);
DECLARE @cnt int = (SELECT COUNT(*) FROM #m);
SELECT TOP (2000000) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS i INTO #n FROM sys.all_columns a CROSS JOIN sys.all_columns b CROSS JOIN sys.all_columns c;
SELECT i, 1 + ABS(CHECKSUM(NEWID())) % @cnt AS rn, ABS(CHECKSUM(NEWID())) % 31536000 AS seg INTO #k FROM #n;
CREATE CLUSTERED INDEX ixk ON #k (rn);
INSERT midia.Reproducao (MidiaId, IniciadoEmUtc)
SELECT m.Id, DATEADD(SECOND, -k.seg, CAST(SYSUTCDATETIME() AS datetime2(0))) FROM #k k JOIN #m m ON m.rn = k.rn OPTION (MAXDOP 1);
-- pedidos: 100k Tocado com mídia, 5k Aprovado manual (sem mídia) e 4 Aprovado com mídia em situações distintas
DECLARE @mid uniqueidentifier = (SELECT TOP 1 Id FROM #m);
;WITH n AS (SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_columns a CROSS JOIN sys.all_columns b)
INSERT interacao.PedidoMusica (NomeOuvinte, TituloMusica, Artista, Status, MidiaId, ModeradoPorUsuarioId, ModeradoEmUtc, EnviadoPlayoutEmUtc, TocadoEmUtc, CriadoEmUtc)
SELECT N'(anônimo)', N'Musica', N'Artista', 4, @mid, @u, DATEADD(MINUTE, -i, SYSUTCDATETIME()), DATEADD(MINUTE, -i, SYSUTCDATETIME()), DATEADD(MINUTE, -i, SYSUTCDATETIME()), DATEADD(MINUTE, -i-5, SYSUTCDATETIME()) FROM n;
;WITH n AS (SELECT TOP (5000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_columns a CROSS JOIN sys.all_columns b)
INSERT interacao.PedidoMusica (NomeOuvinte, TituloMusica, Artista, Status, MidiaId, ModeradoPorUsuarioId, ModeradoEmUtc, CriadoEmUtc)
SELECT N'x', N'Musica', N'Artista', 2, NULL, @u, DATEADD(MINUTE, -i*3, SYSUTCDATETIME()), DATEADD(MINUTE, -i*3-5, SYSUTCDATETIME()) FROM n;
INSERT interacao.PedidoMusica (NomeOuvinte, TituloMusica, Artista, Status, MidiaId, ModeradoPorUsuarioId, ModeradoEmUtc, EnviadoPlayoutEmUtc, CriadoEmUtc)
VALUES (N'a', N'm', N'a', 2, @mid, @u, DATEADD(MINUTE,-50,SYSUTCDATETIME()), DATEADD(MINUTE,-10,SYSUTCDATETIME()), DATEADD(MINUTE,-60,SYSUTCDATETIME())),
       (N'b', N'm', N'a', 2, @mid, @u, DATEADD(MINUTE,-40,SYSUTCDATETIME()), DATEADD(MINUTE,-35,SYSUTCDATETIME()), DATEADD(MINUTE,-60,SYSUTCDATETIME())),
       (N'c', N'm', N'a', 2, @mid, @u, DATEADD(MINUTE,-30,SYSUTCDATETIME()), NULL, DATEADD(MINUTE,-60,SYSUTCDATETIME())),
       (N'd', N'm', N'a', 2, @mid, @u, DATEADD(DAY,-3,SYSUTCDATETIME()), NULL, DATEADD(DAY,-3,SYSUTCDATETIME()));
UPDATE STATISTICS midia.ArquivoMidia WITH FULLSCAN; UPDATE STATISTICS midia.Reproducao WITH FULLSCAN; UPDATE STATISTICS interacao.PedidoMusica WITH FULLSCAN;
SELECT (SELECT COUNT(*) FROM midia.ArquivoMidia) AS midias, (SELECT COUNT(*) FROM midia.Reproducao) AS repro, (SELECT COUNT(*) FROM interacao.PedidoMusica) AS pedidos,
  (SELECT COUNT(*) FROM midia.Reproducao WHERE IniciadoEmUtc >= DATEADD(MINUTE,-90,SYSUTCDATETIME())) AS repro_90min;
