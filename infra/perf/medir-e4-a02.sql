/* =====================================================================
   WebRadio — infra/perf/medir-e4-a02.sql
   Autor: Atlas (DBA) · tarefa E4-A02 (08, E4-D12, DoD 9) · 2026-11-19

   Mede rel.usp_MaisTocadas sobre o dataset de gerar-dataset-e4.sql (50.000 mídias, 2 milhões de Reproducao em 12 meses) nos períodos de
   7, 30 e 366 dias, com @Top = 20 (padrão da API) e 100 (máximo), e compara com a forma ingênua (JOIN antes do TOP). STATISTICS IO, TIME e
   PROFILE (plano real). O cache de dados é esfriado antes de cada período; o de planos não importa (OPTION (RECOMPILE)).
   ===================================================================== */
SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; SET ARITHABORT ON;
GO
DECLARE @dias TABLE (dias int);
INSERT @dias VALUES (7), (30), (366);
SELECT d.dias, COUNT(*) AS reproducoes_no_periodo
FROM @dias d JOIN midia.Reproducao r ON r.IniciadoEmUtc >= DATEADD(DAY, -d.dias, SYSUTCDATETIME()) GROUP BY d.dias ORDER BY d.dias;
GO
PRINT '=== M1 procedure, 7 dias, Top 20 ===';
DECLARE @de datetime2(0) = DATEADD(DAY, -7, SYSUTCDATETIME()), @ate datetime2(0) = SYSUTCDATETIME();
DBCC DROPCLEANBUFFERS;
SET STATISTICS IO ON; SET STATISTICS TIME ON; SET STATISTICS PROFILE ON;
EXEC rel.usp_MaisTocadas @de, @ate, 20;
SET STATISTICS PROFILE OFF; SET STATISTICS TIME OFF; SET STATISTICS IO OFF;
GO
PRINT '=== M2 procedure, 30 dias, Top 20 ===';
DECLARE @de datetime2(0) = DATEADD(DAY, -30, SYSUTCDATETIME()), @ate datetime2(0) = SYSUTCDATETIME();
DBCC DROPCLEANBUFFERS;
SET STATISTICS IO ON; SET STATISTICS TIME ON; SET STATISTICS PROFILE ON;
EXEC rel.usp_MaisTocadas @de, @ate, 20;
SET STATISTICS PROFILE OFF; SET STATISTICS TIME OFF; SET STATISTICS IO OFF;
GO
PRINT '=== M3 procedure, 366 dias, Top 20 ===';
DECLARE @de datetime2(0) = DATEADD(DAY, -366, SYSUTCDATETIME()), @ate datetime2(0) = SYSUTCDATETIME();
DBCC DROPCLEANBUFFERS;
SET STATISTICS IO ON; SET STATISTICS TIME ON; SET STATISTICS PROFILE ON;
EXEC rel.usp_MaisTocadas @de, @ate, 20;
SET STATISTICS PROFILE OFF; SET STATISTICS TIME OFF; SET STATISTICS IO OFF;
GO
PRINT '=== M4 procedure, 366 dias, Top 100 ===';
DECLARE @de datetime2(0) = DATEADD(DAY, -366, SYSUTCDATETIME()), @ate datetime2(0) = SYSUTCDATETIME();
DBCC DROPCLEANBUFFERS;
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC rel.usp_MaisTocadas @de, @ate, 100;
SET STATISTICS TIME OFF; SET STATISTICS IO OFF;
GO
PRINT '=== M5 forma ingenua (JOIN antes do TOP; o rascunho do E1-A07), 30 dias, Top 20 ===';
DECLARE @de datetime2(0) = DATEADD(DAY, -30, SYSUTCDATETIME()), @ate datetime2(0) = SYSUTCDATETIME();
DBCC DROPCLEANBUFFERS;
SET STATISTICS IO ON; SET STATISTICS TIME ON;
SELECT TOP (20) m.Id AS MidiaId, m.Titulo, m.Artista, m.TipoMidia, COUNT(*) AS Reproducoes
FROM midia.Reproducao AS r JOIN midia.ArquivoMidia AS m ON m.Id = r.MidiaId
WHERE r.IniciadoEmUtc >= @de AND r.IniciadoEmUtc < @ate
GROUP BY m.Id, m.Titulo, m.Artista, m.TipoMidia
ORDER BY COUNT(*) DESC, m.Id
OPTION (RECOMPILE);
SET STATISTICS TIME OFF; SET STATISTICS IO OFF;
GO
PRINT '=== M6 procedure, 366 dias, Top 20, cache quente (segunda chamada) ===';
DECLARE @de datetime2(0) = DATEADD(DAY, -366, SYSUTCDATETIME()), @ate datetime2(0) = SYSUTCDATETIME();
EXEC rel.usp_MaisTocadas @de, @ate, 20 WITH RESULT SETS ((MidiaId uniqueidentifier, Titulo nvarchar(200), Artista nvarchar(200), TipoMidia tinyint, Reproducoes int));
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC rel.usp_MaisTocadas @de, @ate, 20;
SET STATISTICS TIME OFF; SET STATISTICS IO OFF;
GO
