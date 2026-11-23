/* =====================================================================
   WebRadio — infra/perf/indices-propostos-e4-a01b.sql
   Autor: Atlas (DBA) · tarefa E4-A01b · 2026-11-23

   Índice que o parecer e4-a01-midia.md propõe para os limites do S-M06. Só para o banco de medição: o rodar-e4-a01b.sh aplica este arquivo entre a
   medição "antes" e a "depois". Vai para o 03 e para uma migration (parecer, seção 7).
   Testado e REJEITADO (parecer, seção 3): (DataUploadUtc DESC, Id) INCLUDE (TipoMidia, StatusSanitizacao, Ativo) para a listagem.
   ===================================================================== */
SET QUOTED_IDENTIFIER ON;
GO
-- Limites do S-M06 (abertos, 20/h, bytes/h, espera): o índice por usuário passa a ter a data na chave e as colunas que as quatro consultas leem.
-- Mesmo nome do IX_ArquivoMidia_EnviadoPor do 03 (a FK continua com índice que começa por EnviadoPorUsuarioId).
DROP INDEX IX_ArquivoMidia_EnviadoPor ON midia.ArquivoMidia;
CREATE INDEX IX_ArquivoMidia_EnviadoPor ON midia.ArquivoMidia (EnviadoPorUsuarioId, DataUploadUtc) INCLUDE (StatusSanitizacao, TamanhoBytes, UploadExpiraEmUtc);
GO
SELECT i.name AS indice, ps.used_page_count AS paginas FROM sys.indexes AS i JOIN sys.dm_db_partition_stats AS ps ON ps.object_id = i.object_id AND ps.index_id = i.index_id
WHERE i.object_id = OBJECT_ID(N'midia.ArquivoMidia') ORDER BY i.index_id;
GO
