/* =====================================================================
   WebRadio — infra/perf/medir-e4-a01b.sql        (GERADO: o texto das consultas vem do código real, ver abaixo)
   Autor: Atlas (DBA) · tarefa E4-A01b (08) · 2026-11-23

   Mede, sobre o dataset de gerar-dataset-e4.sql + gerar-dataset-e4b.sql (50.000 mídias, 2 milhões de Reproducao), as consultas do código REAL:
     C  captura da sanitização (CapturaDeMidia.cs, E4-F05)
     B  biblioteca com filtros e busca (ListarMidiasHandler, ConsultasDeMidia.cs): o SQL é o que o EF Core 10 gera (ToQueryString() do mesmo LINQ,
        com o join em seg.Usuario e as 25 colunas da página), não uma tradução à mão. O COUNT é o `SELECT COUNT(*)` do CountAsync.
     S  os dois limites do S-M06 pelo fallback SQL (IniciarUploadHandler, UploadDeMidia.cs): abertos, 20/h e bytes/h, para o Admin A1
        (10.000 envios, 15 na última hora), o locutor B1 (no limite de 5 abertos) e o locutor C1 (~1.000 envios)
     P  a `proxima` (FilaDoPlayout.cs, E4-F08): pedido (fila vazia e com elegível) e as três buscas da rotação, com a chamada completa
   Cada consulta roda por sp_executesql com os tipos de parâmetro do código (como o EF), então o plano nasce com o parâmetro "farejado".
   O custo vem de STATISTICS IO (leituras lógicas) e TIME. Tudo que escreve roda em transação com ROLLBACK.
   ===================================================================== */
SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; SET ARITHABORT ON;
GO
PRINT '=== contagens do dataset ===';
SELECT (SELECT COUNT(*) FROM midia.ArquivoMidia) AS midias, (SELECT COUNT(*) FROM midia.Reproducao) AS reproducoes, (SELECT COUNT(*) FROM seg.Usuario) AS usuarios,
       (SELECT COUNT(*) FROM midia.ArquivoMidia WHERE StatusSanitizacao = 2) AS pendentes;
SELECT i.name AS indice, i.has_filter AS filtrado, ps.used_page_count AS paginas
FROM sys.indexes AS i JOIN sys.dm_db_partition_stats AS ps ON ps.object_id = i.object_id AND ps.index_id = i.index_id
WHERE i.object_id = OBJECT_ID(N'midia.ArquivoMidia') ORDER BY i.index_id;
GO
PRINT '### C1 captura: fila com 30 pendentes (o dia a dia)';
BEGIN TRAN;

DBCC FREEPROCCACHE;
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SET XACT_ABORT ON;
DECLARE @capturado TABLE (Id uniqueidentifier);

;WITH alvo AS (
    SELECT TOP (1) *
    FROM midia.ArquivoMidia WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE StatusSanitizacao = 2
    ORDER BY DataUploadUtc
)
UPDATE alvo SET StatusSanitizacao = 3, TentativasSanitizacao = TentativasSanitizacao + 1, EmAnaliseDesdeUtc = SYSUTCDATETIME()
OUTPUT inserted.Id INTO @capturado;

SELECT Id AS [Value] FROM @capturado;';
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
ROLLBACK;
GO
PRINT '### C2 captura: fila vazia (a sondagem do Worker quando não há nada)';
BEGIN TRAN;
UPDATE midia.ArquivoMidia SET StatusSanitizacao = 6 WHERE StatusSanitizacao = 2;
DBCC FREEPROCCACHE;
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SET XACT_ABORT ON;
DECLARE @capturado TABLE (Id uniqueidentifier);

;WITH alvo AS (
    SELECT TOP (1) *
    FROM midia.ArquivoMidia WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE StatusSanitizacao = 2
    ORDER BY DataUploadUtc
)
UPDATE alvo SET StatusSanitizacao = 3, TentativasSanitizacao = TentativasSanitizacao + 1, EmAnaliseDesdeUtc = SYSUTCDATETIME()
OUTPUT inserted.Id INTO @capturado;

SELECT Id AS [Value] FROM @capturado;';
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
ROLLBACK;
GO
PRINT '### C3 captura: rajada de 1.000 pendentes (importação em massa)';
BEGIN TRAN;
UPDATE TOP (1000) midia.ArquivoMidia SET StatusSanitizacao = 2 WHERE StatusSanitizacao = 4 AND EnviadoPorUsuarioId = '00000000-0000-0000-0000-0000000000A1';
DBCC FREEPROCCACHE;
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SET XACT_ABORT ON;
DECLARE @capturado TABLE (Id uniqueidentifier);

;WITH alvo AS (
    SELECT TOP (1) *
    FROM midia.ArquivoMidia WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE StatusSanitizacao = 2
    ORDER BY DataUploadUtc
)
UPDATE alvo SET StatusSanitizacao = 3, TentativasSanitizacao = TentativasSanitizacao + 1, EmAnaliseDesdeUtc = SYSUTCDATETIME()
OUTPUT inserted.Id INTO @capturado;

SELECT Id AS [Value] FROM @capturado;';
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
ROLLBACK;
GO
PRINT '=== B biblioteca (ListarMidiasHandler): COUNT + página de 20, pelo EF ===';
GO
DBCC FREEPROCCACHE;
GO
PRINT '### L1 admin, sem filtro, página 1 — COUNT';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT COUNT(*)
FROM [midia].[ArquivoMidia] AS [a]
';
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### L1 admin, sem filtro, página 1 — página';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a0].[Id], [a0].[Artista], [a0].[Ativo], [a0].[Bucket], [a0].[ChaveStorage], [a0].[DataUploadUtc], [a0].[DuracaoSegundos], [a0].[EmAnaliseDesdeUtc], [a0].[EnviadoPorUsuarioId], [a0].[EtagUpload], [a0].[HashOriginalSHA256], [a0].[HashSHA256], [a0].[MimeType], [a0].[MimeTypeDeclarado], [a0].[MotivoRejeicao], [a0].[NomeOriginal], [a0].[RowVersion], [a0].[SanitizadoEmUtc], [a0].[Sorteio], [a0].[StatusSanitizacao], [a0].[TamanhoBytes], [a0].[TentativasSanitizacao], [a0].[TipoMidia], [a0].[Titulo], [a0].[UploadExpiraEmUtc], [u].[Nome]
FROM (
    SELECT [a].[Id], [a].[Artista], [a].[Ativo], [a].[Bucket], [a].[ChaveStorage], [a].[DataUploadUtc], [a].[DuracaoSegundos], [a].[EmAnaliseDesdeUtc], [a].[EnviadoPorUsuarioId], [a].[EtagUpload], [a].[HashOriginalSHA256], [a].[HashSHA256], [a].[MimeType], [a].[MimeTypeDeclarado], [a].[MotivoRejeicao], [a].[NomeOriginal], [a].[RowVersion], [a].[SanitizadoEmUtc], [a].[Sorteio], [a].[StatusSanitizacao], [a].[TamanhoBytes], [a].[TentativasSanitizacao], [a].[TipoMidia], [a].[Titulo], [a].[UploadExpiraEmUtc]
    FROM [midia].[ArquivoMidia] AS [a]
    
    ORDER BY [a].[DataUploadUtc] DESC, [a].[Id]
    OFFSET @pa ROWS FETCH NEXT @pb ROWS ONLY
) AS [a0]
LEFT JOIN [seg].[Usuario] AS [u] ON [a0].[EnviadoPorUsuarioId] = [u].[Id]
ORDER BY [a0].[DataUploadUtc] DESC, [a0].[Id]', N'@pa int, @pb int', @pa = 0, @pb = 20;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### L1b admin, sem filtro, página 2.000 (OFFSET 39.980) — COUNT';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT COUNT(*)
FROM [midia].[ArquivoMidia] AS [a]
';
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### L1b admin, sem filtro, página 2.000 (OFFSET 39.980) — página';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a0].[Id], [a0].[Artista], [a0].[Ativo], [a0].[Bucket], [a0].[ChaveStorage], [a0].[DataUploadUtc], [a0].[DuracaoSegundos], [a0].[EmAnaliseDesdeUtc], [a0].[EnviadoPorUsuarioId], [a0].[EtagUpload], [a0].[HashOriginalSHA256], [a0].[HashSHA256], [a0].[MimeType], [a0].[MimeTypeDeclarado], [a0].[MotivoRejeicao], [a0].[NomeOriginal], [a0].[RowVersion], [a0].[SanitizadoEmUtc], [a0].[Sorteio], [a0].[StatusSanitizacao], [a0].[TamanhoBytes], [a0].[TentativasSanitizacao], [a0].[TipoMidia], [a0].[Titulo], [a0].[UploadExpiraEmUtc], [u].[Nome]
FROM (
    SELECT [a].[Id], [a].[Artista], [a].[Ativo], [a].[Bucket], [a].[ChaveStorage], [a].[DataUploadUtc], [a].[DuracaoSegundos], [a].[EmAnaliseDesdeUtc], [a].[EnviadoPorUsuarioId], [a].[EtagUpload], [a].[HashOriginalSHA256], [a].[HashSHA256], [a].[MimeType], [a].[MimeTypeDeclarado], [a].[MotivoRejeicao], [a].[NomeOriginal], [a].[RowVersion], [a].[SanitizadoEmUtc], [a].[Sorteio], [a].[StatusSanitizacao], [a].[TamanhoBytes], [a].[TentativasSanitizacao], [a].[TipoMidia], [a].[Titulo], [a].[UploadExpiraEmUtc]
    FROM [midia].[ArquivoMidia] AS [a]
    
    ORDER BY [a].[DataUploadUtc] DESC, [a].[Id]
    OFFSET @pa ROWS FETCH NEXT @pb ROWS ONLY
) AS [a0]
LEFT JOIN [seg].[Usuario] AS [u] ON [a0].[EnviadoPorUsuarioId] = [u].[Id]
ORDER BY [a0].[DataUploadUtc] DESC, [a0].[Id]', N'@pa int, @pb int', @pa = 39980, @pb = 20;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### L2 locutor, tipo Música (só ativas) — COUNT';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT COUNT(*)
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[TipoMidia] = @t AND [a].[Ativo] = CAST(1 AS bit)', N'@t tinyint', @t = 3;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### L2 locutor, tipo Música (só ativas) — página';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a0].[Id], [a0].[Artista], [a0].[Ativo], [a0].[Bucket], [a0].[ChaveStorage], [a0].[DataUploadUtc], [a0].[DuracaoSegundos], [a0].[EmAnaliseDesdeUtc], [a0].[EnviadoPorUsuarioId], [a0].[EtagUpload], [a0].[HashOriginalSHA256], [a0].[HashSHA256], [a0].[MimeType], [a0].[MimeTypeDeclarado], [a0].[MotivoRejeicao], [a0].[NomeOriginal], [a0].[RowVersion], [a0].[SanitizadoEmUtc], [a0].[Sorteio], [a0].[StatusSanitizacao], [a0].[TamanhoBytes], [a0].[TentativasSanitizacao], [a0].[TipoMidia], [a0].[Titulo], [a0].[UploadExpiraEmUtc], [u].[Nome]
FROM (
    SELECT [a].[Id], [a].[Artista], [a].[Ativo], [a].[Bucket], [a].[ChaveStorage], [a].[DataUploadUtc], [a].[DuracaoSegundos], [a].[EmAnaliseDesdeUtc], [a].[EnviadoPorUsuarioId], [a].[EtagUpload], [a].[HashOriginalSHA256], [a].[HashSHA256], [a].[MimeType], [a].[MimeTypeDeclarado], [a].[MotivoRejeicao], [a].[NomeOriginal], [a].[RowVersion], [a].[SanitizadoEmUtc], [a].[Sorteio], [a].[StatusSanitizacao], [a].[TamanhoBytes], [a].[TentativasSanitizacao], [a].[TipoMidia], [a].[Titulo], [a].[UploadExpiraEmUtc]
    FROM [midia].[ArquivoMidia] AS [a]
    WHERE [a].[TipoMidia] = @t AND [a].[Ativo] = CAST(1 AS bit)
    ORDER BY [a].[DataUploadUtc] DESC, [a].[Id]
    OFFSET @pa ROWS FETCH NEXT @pb ROWS ONLY
) AS [a0]
LEFT JOIN [seg].[Usuario] AS [u] ON [a0].[EnviadoPorUsuarioId] = [u].[Id]
ORDER BY [a0].[DataUploadUtc] DESC, [a0].[Id]', N'@t tinyint, @pa int, @pb int', @t = 3, @pa = 0, @pb = 20;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### L3 admin, tipo Música, status Aprovado, ativo — COUNT';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT COUNT(*)
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[TipoMidia] = @t AND [a].[StatusSanitizacao] = @s AND [a].[Ativo] = @a', N'@t tinyint, @s tinyint, @a bit', @t = 3, @s = 4, @a = 1;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### L3 admin, tipo Música, status Aprovado, ativo — página';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a0].[Id], [a0].[Artista], [a0].[Ativo], [a0].[Bucket], [a0].[ChaveStorage], [a0].[DataUploadUtc], [a0].[DuracaoSegundos], [a0].[EmAnaliseDesdeUtc], [a0].[EnviadoPorUsuarioId], [a0].[EtagUpload], [a0].[HashOriginalSHA256], [a0].[HashSHA256], [a0].[MimeType], [a0].[MimeTypeDeclarado], [a0].[MotivoRejeicao], [a0].[NomeOriginal], [a0].[RowVersion], [a0].[SanitizadoEmUtc], [a0].[Sorteio], [a0].[StatusSanitizacao], [a0].[TamanhoBytes], [a0].[TentativasSanitizacao], [a0].[TipoMidia], [a0].[Titulo], [a0].[UploadExpiraEmUtc], [u].[Nome]
FROM (
    SELECT [a].[Id], [a].[Artista], [a].[Ativo], [a].[Bucket], [a].[ChaveStorage], [a].[DataUploadUtc], [a].[DuracaoSegundos], [a].[EmAnaliseDesdeUtc], [a].[EnviadoPorUsuarioId], [a].[EtagUpload], [a].[HashOriginalSHA256], [a].[HashSHA256], [a].[MimeType], [a].[MimeTypeDeclarado], [a].[MotivoRejeicao], [a].[NomeOriginal], [a].[RowVersion], [a].[SanitizadoEmUtc], [a].[Sorteio], [a].[StatusSanitizacao], [a].[TamanhoBytes], [a].[TentativasSanitizacao], [a].[TipoMidia], [a].[Titulo], [a].[UploadExpiraEmUtc]
    FROM [midia].[ArquivoMidia] AS [a]
    WHERE [a].[TipoMidia] = @t AND [a].[StatusSanitizacao] = @s AND [a].[Ativo] = @a
    ORDER BY [a].[DataUploadUtc] DESC, [a].[Id]
    OFFSET @pa ROWS FETCH NEXT @pb ROWS ONLY
) AS [a0]
LEFT JOIN [seg].[Usuario] AS [u] ON [a0].[EnviadoPorUsuarioId] = [u].[Id]
ORDER BY [a0].[DataUploadUtc] DESC, [a0].[Id]', N'@t tinyint, @s tinyint, @a bit, @pa int, @pb int', @t = 3, @s = 4, @a = 1, @pa = 0, @pb = 20;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### L3b admin, status Pendente (a tela da fila) — COUNT';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT COUNT(*)
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[StatusSanitizacao] = @s', N'@s tinyint', @s = 2;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### L3b admin, status Pendente (a tela da fila) — página';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a0].[Id], [a0].[Artista], [a0].[Ativo], [a0].[Bucket], [a0].[ChaveStorage], [a0].[DataUploadUtc], [a0].[DuracaoSegundos], [a0].[EmAnaliseDesdeUtc], [a0].[EnviadoPorUsuarioId], [a0].[EtagUpload], [a0].[HashOriginalSHA256], [a0].[HashSHA256], [a0].[MimeType], [a0].[MimeTypeDeclarado], [a0].[MotivoRejeicao], [a0].[NomeOriginal], [a0].[RowVersion], [a0].[SanitizadoEmUtc], [a0].[Sorteio], [a0].[StatusSanitizacao], [a0].[TamanhoBytes], [a0].[TentativasSanitizacao], [a0].[TipoMidia], [a0].[Titulo], [a0].[UploadExpiraEmUtc], [u].[Nome]
FROM (
    SELECT [a].[Id], [a].[Artista], [a].[Ativo], [a].[Bucket], [a].[ChaveStorage], [a].[DataUploadUtc], [a].[DuracaoSegundos], [a].[EmAnaliseDesdeUtc], [a].[EnviadoPorUsuarioId], [a].[EtagUpload], [a].[HashOriginalSHA256], [a].[HashSHA256], [a].[MimeType], [a].[MimeTypeDeclarado], [a].[MotivoRejeicao], [a].[NomeOriginal], [a].[RowVersion], [a].[SanitizadoEmUtc], [a].[Sorteio], [a].[StatusSanitizacao], [a].[TamanhoBytes], [a].[TentativasSanitizacao], [a].[TipoMidia], [a].[Titulo], [a].[UploadExpiraEmUtc]
    FROM [midia].[ArquivoMidia] AS [a]
    WHERE [a].[StatusSanitizacao] = @s
    ORDER BY [a].[DataUploadUtc] DESC, [a].[Id]
    OFFSET @pa ROWS FETCH NEXT @pb ROWS ONLY
) AS [a0]
LEFT JOIN [seg].[Usuario] AS [u] ON [a0].[EnviadoPorUsuarioId] = [u].[Id]
ORDER BY [a0].[DataUploadUtc] DESC, [a0].[Id]', N'@s tinyint, @pa int, @pb int', @s = 2, @pa = 0, @pb = 20;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### L4 locutor, busca rara (''numero 1234'') — COUNT';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT COUNT(*)
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[Ativo] = CAST(1 AS bit) AND ([a].[Titulo] LIKE @p ESCAPE N''\'' OR [a].[Artista] LIKE @p ESCAPE N''\'' OR [a].[NomeOriginal] LIKE @p0 ESCAPE N''\'')', N'@p nvarchar(200), @p0 nvarchar(255)', @p = N'%numero 1234%', @p0 = N'%numero 1234%';
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### L4 locutor, busca rara (''numero 1234'') — página';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a0].[Id], [a0].[Artista], [a0].[Ativo], [a0].[Bucket], [a0].[ChaveStorage], [a0].[DataUploadUtc], [a0].[DuracaoSegundos], [a0].[EmAnaliseDesdeUtc], [a0].[EnviadoPorUsuarioId], [a0].[EtagUpload], [a0].[HashOriginalSHA256], [a0].[HashSHA256], [a0].[MimeType], [a0].[MimeTypeDeclarado], [a0].[MotivoRejeicao], [a0].[NomeOriginal], [a0].[RowVersion], [a0].[SanitizadoEmUtc], [a0].[Sorteio], [a0].[StatusSanitizacao], [a0].[TamanhoBytes], [a0].[TentativasSanitizacao], [a0].[TipoMidia], [a0].[Titulo], [a0].[UploadExpiraEmUtc], [u].[Nome]
FROM (
    SELECT [a].[Id], [a].[Artista], [a].[Ativo], [a].[Bucket], [a].[ChaveStorage], [a].[DataUploadUtc], [a].[DuracaoSegundos], [a].[EmAnaliseDesdeUtc], [a].[EnviadoPorUsuarioId], [a].[EtagUpload], [a].[HashOriginalSHA256], [a].[HashSHA256], [a].[MimeType], [a].[MimeTypeDeclarado], [a].[MotivoRejeicao], [a].[NomeOriginal], [a].[RowVersion], [a].[SanitizadoEmUtc], [a].[Sorteio], [a].[StatusSanitizacao], [a].[TamanhoBytes], [a].[TentativasSanitizacao], [a].[TipoMidia], [a].[Titulo], [a].[UploadExpiraEmUtc]
    FROM [midia].[ArquivoMidia] AS [a]
    WHERE [a].[Ativo] = CAST(1 AS bit) AND ([a].[Titulo] LIKE @p ESCAPE N''\'' OR [a].[Artista] LIKE @p ESCAPE N''\'' OR [a].[NomeOriginal] LIKE @p0 ESCAPE N''\'')
    ORDER BY [a].[DataUploadUtc] DESC, [a].[Id]
    OFFSET @pa ROWS FETCH NEXT @pb ROWS ONLY
) AS [a0]
LEFT JOIN [seg].[Usuario] AS [u] ON [a0].[EnviadoPorUsuarioId] = [u].[Id]
ORDER BY [a0].[DataUploadUtc] DESC, [a0].[Id]', N'@p nvarchar(200), @p0 nvarchar(255), @pa int, @pb int', @p = N'%numero 1234%', @p0 = N'%numero 1234%', @pa = 0, @pb = 20;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### L5 admin, tipo Música, busca que casa tudo (''ao vivo'') — COUNT';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT COUNT(*)
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[TipoMidia] = @t AND ([a].[Titulo] LIKE @p ESCAPE N''\'' OR [a].[Artista] LIKE @p ESCAPE N''\'' OR [a].[NomeOriginal] LIKE @p0 ESCAPE N''\'')', N'@t tinyint, @p nvarchar(200), @p0 nvarchar(255)', @t = 3, @p = N'%ao vivo%', @p0 = N'%ao vivo%';
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### L5 admin, tipo Música, busca que casa tudo (''ao vivo'') — página';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a0].[Id], [a0].[Artista], [a0].[Ativo], [a0].[Bucket], [a0].[ChaveStorage], [a0].[DataUploadUtc], [a0].[DuracaoSegundos], [a0].[EmAnaliseDesdeUtc], [a0].[EnviadoPorUsuarioId], [a0].[EtagUpload], [a0].[HashOriginalSHA256], [a0].[HashSHA256], [a0].[MimeType], [a0].[MimeTypeDeclarado], [a0].[MotivoRejeicao], [a0].[NomeOriginal], [a0].[RowVersion], [a0].[SanitizadoEmUtc], [a0].[Sorteio], [a0].[StatusSanitizacao], [a0].[TamanhoBytes], [a0].[TentativasSanitizacao], [a0].[TipoMidia], [a0].[Titulo], [a0].[UploadExpiraEmUtc], [u].[Nome]
FROM (
    SELECT [a].[Id], [a].[Artista], [a].[Ativo], [a].[Bucket], [a].[ChaveStorage], [a].[DataUploadUtc], [a].[DuracaoSegundos], [a].[EmAnaliseDesdeUtc], [a].[EnviadoPorUsuarioId], [a].[EtagUpload], [a].[HashOriginalSHA256], [a].[HashSHA256], [a].[MimeType], [a].[MimeTypeDeclarado], [a].[MotivoRejeicao], [a].[NomeOriginal], [a].[RowVersion], [a].[SanitizadoEmUtc], [a].[Sorteio], [a].[StatusSanitizacao], [a].[TamanhoBytes], [a].[TentativasSanitizacao], [a].[TipoMidia], [a].[Titulo], [a].[UploadExpiraEmUtc]
    FROM [midia].[ArquivoMidia] AS [a]
    WHERE [a].[TipoMidia] = @t AND ([a].[Titulo] LIKE @p ESCAPE N''\'' OR [a].[Artista] LIKE @p ESCAPE N''\'' OR [a].[NomeOriginal] LIKE @p0 ESCAPE N''\'')
    ORDER BY [a].[DataUploadUtc] DESC, [a].[Id]
    OFFSET @pa ROWS FETCH NEXT @pb ROWS ONLY
) AS [a0]
LEFT JOIN [seg].[Usuario] AS [u] ON [a0].[EnviadoPorUsuarioId] = [u].[Id]
ORDER BY [a0].[DataUploadUtc] DESC, [a0].[Id]', N'@t tinyint, @p nvarchar(200), @p0 nvarchar(255), @pa int, @pb int', @t = 3, @p = N'%ao vivo%', @p0 = N'%ao vivo%', @pa = 0, @pb = 20;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '=== S limites do S-M06 pelo fallback SQL (IniciarUploadHandler) ===';
GO
DBCC FREEPROCCACHE;
GO
DECLARE @agora datetime2(7) = SYSUTCDATETIME(), @corte datetime2(7) = DATEADD(HOUR, -1, SYSUTCDATETIME());
PRINT '--- usuário A1 Admin (10.000 envios, 15 na hora)';
PRINT '### S1 abertos (CountAsync)';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT COUNT(*)
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[EnviadoPorUsuarioId] = @uid AND [a].[StatusSanitizacao] IN (@abertos1, @abertos2, @abertos3) AND ([a].[StatusSanitizacao] <> CAST(1 AS tinyint) OR [a].[UploadExpiraEmUtc] > @agora)', N'@uid uniqueidentifier, @abertos1 tinyint, @abertos2 tinyint, @abertos3 tinyint, @agora datetime2', @uid = '00000000-0000-0000-0000-0000000000A1', @abertos1 = 1, @abertos2 = 2, @abertos3 = 3, @agora = @agora;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
PRINT '### S2 20/h (datas da hora)';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a].[DataUploadUtc]
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[EnviadoPorUsuarioId] = @uid AND [a].[DataUploadUtc] > @corte
ORDER BY [a].[DataUploadUtc]', N'@uid uniqueidentifier, @corte datetime2', @uid = '00000000-0000-0000-0000-0000000000A1', @corte = @corte;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
PRINT '### S3 bytes/h (data e tamanho da hora)';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a].[DataUploadUtc], [a].[TamanhoBytes]
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[EnviadoPorUsuarioId] = @uid AND [a].[DataUploadUtc] > @corte
ORDER BY [a].[DataUploadUtc]', N'@uid uniqueidentifier, @corte datetime2', @uid = '00000000-0000-0000-0000-0000000000A1', @corte = @corte;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
PRINT '### S4 espera dos abertos (vencimento do mais próximo)';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a].[UploadExpiraEmUtc]
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[EnviadoPorUsuarioId] = @uid AND [a].[StatusSanitizacao] = CAST(1 AS tinyint) AND [a].[UploadExpiraEmUtc] > @agora', N'@uid uniqueidentifier, @agora datetime2', @uid = '00000000-0000-0000-0000-0000000000A1', @agora = @agora;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
DECLARE @agora datetime2(7) = SYSUTCDATETIME(), @corte datetime2(7) = DATEADD(HOUR, -1, SYSUTCDATETIME());
PRINT '--- usuário B1 locutor no limite (5 abertos)';
PRINT '### S1 abertos (CountAsync)';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT COUNT(*)
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[EnviadoPorUsuarioId] = @uid AND [a].[StatusSanitizacao] IN (@abertos1, @abertos2, @abertos3) AND ([a].[StatusSanitizacao] <> CAST(1 AS tinyint) OR [a].[UploadExpiraEmUtc] > @agora)', N'@uid uniqueidentifier, @abertos1 tinyint, @abertos2 tinyint, @abertos3 tinyint, @agora datetime2', @uid = '00000000-0000-0000-0000-0000000000B1', @abertos1 = 1, @abertos2 = 2, @abertos3 = 3, @agora = @agora;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
PRINT '### S2 20/h (datas da hora)';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a].[DataUploadUtc]
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[EnviadoPorUsuarioId] = @uid AND [a].[DataUploadUtc] > @corte
ORDER BY [a].[DataUploadUtc]', N'@uid uniqueidentifier, @corte datetime2', @uid = '00000000-0000-0000-0000-0000000000B1', @corte = @corte;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
PRINT '### S3 bytes/h (data e tamanho da hora)';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a].[DataUploadUtc], [a].[TamanhoBytes]
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[EnviadoPorUsuarioId] = @uid AND [a].[DataUploadUtc] > @corte
ORDER BY [a].[DataUploadUtc]', N'@uid uniqueidentifier, @corte datetime2', @uid = '00000000-0000-0000-0000-0000000000B1', @corte = @corte;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
PRINT '### S4 espera dos abertos (vencimento do mais próximo)';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a].[UploadExpiraEmUtc]
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[EnviadoPorUsuarioId] = @uid AND [a].[StatusSanitizacao] = CAST(1 AS tinyint) AND [a].[UploadExpiraEmUtc] > @agora', N'@uid uniqueidentifier, @agora datetime2', @uid = '00000000-0000-0000-0000-0000000000B1', @agora = @agora;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
DECLARE @agora datetime2(7) = SYSUTCDATETIME(), @corte datetime2(7) = DATEADD(HOUR, -1, SYSUTCDATETIME());
PRINT '--- usuário C1 locutor (~1.000 envios)';
PRINT '### S1 abertos (CountAsync)';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT COUNT(*)
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[EnviadoPorUsuarioId] = @uid AND [a].[StatusSanitizacao] IN (@abertos1, @abertos2, @abertos3) AND ([a].[StatusSanitizacao] <> CAST(1 AS tinyint) OR [a].[UploadExpiraEmUtc] > @agora)', N'@uid uniqueidentifier, @abertos1 tinyint, @abertos2 tinyint, @abertos3 tinyint, @agora datetime2', @uid = '00000000-0000-0000-0000-0000000000C1', @abertos1 = 1, @abertos2 = 2, @abertos3 = 3, @agora = @agora;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
PRINT '### S2 20/h (datas da hora)';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a].[DataUploadUtc]
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[EnviadoPorUsuarioId] = @uid AND [a].[DataUploadUtc] > @corte
ORDER BY [a].[DataUploadUtc]', N'@uid uniqueidentifier, @corte datetime2', @uid = '00000000-0000-0000-0000-0000000000C1', @corte = @corte;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
PRINT '### S3 bytes/h (data e tamanho da hora)';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a].[DataUploadUtc], [a].[TamanhoBytes]
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[EnviadoPorUsuarioId] = @uid AND [a].[DataUploadUtc] > @corte
ORDER BY [a].[DataUploadUtc]', N'@uid uniqueidentifier, @corte datetime2', @uid = '00000000-0000-0000-0000-0000000000C1', @corte = @corte;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
PRINT '### S4 espera dos abertos (vencimento do mais próximo)';
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT [a].[UploadExpiraEmUtc]
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[EnviadoPorUsuarioId] = @uid AND [a].[StatusSanitizacao] = CAST(1 AS tinyint) AND [a].[UploadExpiraEmUtc] > @agora', N'@uid uniqueidentifier, @agora datetime2', @uid = '00000000-0000-0000-0000-0000000000C1', @agora = @agora;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '=== P proxima (FilaDoPlayout): pedido e rotação ===';
GO
DBCC FREEPROCCACHE;
GO
PRINT '### P1 pedido com elegível';
DECLARE @agora datetime2(3) = SYSUTCDATETIME(), @corte datetime2(3) = DATEADD(HOUR, -24, SYSUTCDATETIME()), @inst uniqueidentifier = NEWID();
BEGIN TRAN;
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SET XACT_ABORT ON;
DECLARE @capturado TABLE (PedidoId uniqueidentifier, MidiaId uniqueidentifier);

;WITH c AS (
    SELECT TOP (1) p.Id, p.MidiaId, p.EnviadoPlayoutEmUtc, p.EnviadoPlayoutInstancia
    FROM interacao.PedidoMusica AS p WITH (UPDLOCK, READPAST, ROWLOCK, INDEX(IX_PedidoMusica_Playout))
    WHERE p.Status = 2 AND p.MidiaId IS NOT NULL AND p.ModeradoEmUtc >= @corte24h
      AND (p.EnviadoPlayoutEmUtc IS NULL OR p.EnviadoPlayoutInstancia IS NULL OR p.EnviadoPlayoutInstancia <> @instancia)
      AND EXISTS (SELECT 1 FROM midia.ArquivoMidia AS m WHERE m.Id = p.MidiaId AND m.StatusSanitizacao = 4 AND m.Ativo = 1)
    ORDER BY p.ModeradoEmUtc)
UPDATE c SET EnviadoPlayoutEmUtc = @agora, EnviadoPlayoutInstancia = @instancia
OUTPUT inserted.Id, inserted.MidiaId INTO @capturado;

SELECT PedidoId, MidiaId FROM @capturado;', N'@corte24h datetime2(3), @agora datetime2(3), @instancia uniqueidentifier', @corte24h = @corte, @agora = @agora, @instancia = @inst;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
ROLLBACK;
GO
PRINT '### P2 pedido: fila sem elegível (a chamada comum: uma por faixa)';
DECLARE @agora datetime2(3) = SYSUTCDATETIME(), @corte datetime2(3) = DATEADD(HOUR, -24, SYSUTCDATETIME()), @inst uniqueidentifier = NEWID();
BEGIN TRAN;
UPDATE interacao.PedidoMusica SET EnviadoPlayoutEmUtc = @agora, EnviadoPlayoutInstancia = @inst WHERE Status = 2 AND MidiaId IS NOT NULL;
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SET XACT_ABORT ON;
DECLARE @capturado TABLE (PedidoId uniqueidentifier, MidiaId uniqueidentifier);

;WITH c AS (
    SELECT TOP (1) p.Id, p.MidiaId, p.EnviadoPlayoutEmUtc, p.EnviadoPlayoutInstancia
    FROM interacao.PedidoMusica AS p WITH (UPDLOCK, READPAST, ROWLOCK, INDEX(IX_PedidoMusica_Playout))
    WHERE p.Status = 2 AND p.MidiaId IS NOT NULL AND p.ModeradoEmUtc >= @corte24h
      AND (p.EnviadoPlayoutEmUtc IS NULL OR p.EnviadoPlayoutInstancia IS NULL OR p.EnviadoPlayoutInstancia <> @instancia)
      AND EXISTS (SELECT 1 FROM midia.ArquivoMidia AS m WHERE m.Id = p.MidiaId AND m.StatusSanitizacao = 4 AND m.Ativo = 1)
    ORDER BY p.ModeradoEmUtc)
UPDATE c SET EnviadoPlayoutEmUtc = @agora, EnviadoPlayoutInstancia = @instancia
OUTPUT inserted.Id, inserted.MidiaId INTO @capturado;

SELECT PedidoId, MidiaId FROM @capturado;', N'@corte24h datetime2(3), @agora datetime2(3), @instancia uniqueidentifier', @corte24h = @corte, @agora = @agora, @instancia = @inst;
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
ROLLBACK;
GO
PRINT '### R1 rotação Música, Sorteio >= @r';
DECLARE @r int = CHECKSUM(NEWID()), @cj datetime2(0) = DATEADD(MINUTE, -90, SYSUTCDATETIME());
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT TOP (1) a.Id, a.Titulo, a.Artista
FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio >= @r AND a.Id <> @emVoo
  AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo))
                  WHERE r.IniciadoEmUtc >= @corteJanela AND r.MidiaId = a.Id)
ORDER BY a.Sorteio;', N'@tipo tinyint, @r int, @corteJanela datetime2(0), @emVoo uniqueidentifier', @tipo = 3, @r = @r, @corteJanela = @cj, @emVoo = '00000000-0000-0000-0000-000000000000';
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### R2 rotação Música, volta ao início (Sorteio < @r)';
DECLARE @r int = CHECKSUM(NEWID()), @cj datetime2(0) = DATEADD(MINUTE, -90, SYSUTCDATETIME());
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT TOP (1) a.Id, a.Titulo, a.Artista
FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio < @r AND a.Id <> @emVoo
  AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo))
                  WHERE r.IniciadoEmUtc >= @corteJanela AND r.MidiaId = a.Id)
ORDER BY a.Sorteio;', N'@tipo tinyint, @r int, @corteJanela datetime2(0), @emVoo uniqueidentifier', @tipo = 3, @r = @r, @corteJanela = @cj, @emVoo = '00000000-0000-0000-0000-000000000000';
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### R3 conjunto recente (mais antiga dentro da janela)';
DECLARE @cj datetime2(0) = DATEADD(MINUTE, -90, SYSUTCDATETIME());
SET STATISTICS IO ON; SET STATISTICS TIME ON;
EXEC sp_executesql N'SELECT TOP (1) a.Id, a.Titulo, a.Artista
FROM (SELECT r.MidiaId, MAX(r.IniciadoEmUtc) AS Ultima
      FROM midia.Reproducao AS r WHERE r.IniciadoEmUtc >= @corteJanela GROUP BY r.MidiaId) AS u
JOIN midia.ArquivoMidia AS a ON a.Id = u.MidiaId
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Id <> @emVoo
ORDER BY u.Ultima, a.Id;', N'@tipo tinyint, @r int, @corteJanela datetime2(0), @emVoo uniqueidentifier', @tipo = 3, @r = 0, @corteJanela = @cj, @emVoo = '00000000-0000-0000-0000-000000000000';
SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
GO
PRINT '### P3 chamada COMPLETA da rotação, 300 sorteios: pedido vazio + R1 (+ R2 se vazia) + carga da faixa pela PK (critério do A01b: < 50 leituras lógicas)';
DBCC FREEPROCCACHE;
DECLARE @agora datetime2(3) = SYSUTCDATETIME(), @corte datetime2(3) = DATEADD(HOUR, -24, SYSUTCDATETIME()), @inst uniqueidentifier = NEWID();
DECLARE @cj datetime2(0) = DATEADD(MINUTE, -90, @agora), @r int, @id uniqueidentifier, @i int = 0;
DECLARE @ped TABLE (PedidoId uniqueidentifier, MidiaId uniqueidentifier);
DECLARE @t TABLE (Id uniqueidentifier, Titulo nvarchar(200), Artista nvarchar(200));
BEGIN TRAN;
UPDATE interacao.PedidoMusica SET EnviadoPlayoutEmUtc = @agora, EnviadoPlayoutInstancia = @inst WHERE Status = 2 AND MidiaId IS NOT NULL;
WHILE @i < 300
BEGIN
    SET @i += 1; SET @r = CHECKSUM(NEWID()); SET @id = NULL;
    INSERT @ped EXEC sp_executesql N'SET XACT_ABORT ON;
DECLARE @capturado TABLE (PedidoId uniqueidentifier, MidiaId uniqueidentifier);

;WITH c AS (
    SELECT TOP (1) p.Id, p.MidiaId, p.EnviadoPlayoutEmUtc, p.EnviadoPlayoutInstancia
    FROM interacao.PedidoMusica AS p WITH (UPDLOCK, READPAST, ROWLOCK, INDEX(IX_PedidoMusica_Playout))
    WHERE p.Status = 2 AND p.MidiaId IS NOT NULL AND p.ModeradoEmUtc >= @corte24h
      AND (p.EnviadoPlayoutEmUtc IS NULL OR p.EnviadoPlayoutInstancia IS NULL OR p.EnviadoPlayoutInstancia <> @instancia)
      AND EXISTS (SELECT 1 FROM midia.ArquivoMidia AS m WHERE m.Id = p.MidiaId AND m.StatusSanitizacao = 4 AND m.Ativo = 1)
    ORDER BY p.ModeradoEmUtc)
UPDATE c SET EnviadoPlayoutEmUtc = @agora, EnviadoPlayoutInstancia = @instancia
OUTPUT inserted.Id, inserted.MidiaId INTO @capturado;

SELECT PedidoId, MidiaId FROM @capturado;', N'@corte24h datetime2(3), @agora datetime2(3), @instancia uniqueidentifier', @corte24h = @corte, @agora = @agora, @instancia = @inst;
    DELETE @t;
    INSERT @t EXEC sp_executesql N'SELECT TOP (1) a.Id, a.Titulo, a.Artista
FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio >= @r AND a.Id <> @emVoo
  AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo))
                  WHERE r.IniciadoEmUtc >= @corteJanela AND r.MidiaId = a.Id)
ORDER BY a.Sorteio;', N'@tipo tinyint, @r int, @corteJanela datetime2(0), @emVoo uniqueidentifier', @tipo = 3, @r = @r, @corteJanela = @cj, @emVoo = '00000000-0000-0000-0000-000000000000';
    IF NOT EXISTS (SELECT 1 FROM @t) INSERT @t EXEC sp_executesql N'SELECT TOP (1) a.Id, a.Titulo, a.Artista
FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio < @r AND a.Id <> @emVoo
  AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo))
                  WHERE r.IniciadoEmUtc >= @corteJanela AND r.MidiaId = a.Id)
ORDER BY a.Sorteio;', N'@tipo tinyint, @r int, @corteJanela datetime2(0), @emVoo uniqueidentifier', @tipo = 3, @r = @r, @corteJanela = @cj, @emVoo = '00000000-0000-0000-0000-000000000000';
    SELECT TOP (1) @id = Id FROM @t;
    EXEC sp_executesql N'SELECT a.Bucket, a.ChaveStorage, a.Titulo, a.Artista
FROM [midia].[ArquivoMidia] AS [a]
WHERE [a].[Id] = @id AND [a].[StatusSanitizacao] = CAST(4 AS tinyint) AND [a].[Ativo] = CAST(1 AS bit)', N'@id uniqueidentifier', @id = @id;
END
ROLLBACK;
SELECT CASE WHEN t.text LIKE '%IX_PedidoMusica_Playout%' THEN 'pedido' WHEN t.text LIKE '%Sorteio >= @r%' THEN 'R1 (>= @r)' WHEN t.text LIKE '%Sorteio < @r%' THEN 'R2 (< @r)'
            WHEN t.text LIKE '%SELECT a.Bucket, a.ChaveStorage%' THEN 'carga pela PK' END AS instrucao,
       MAX(s.execution_count) AS execucoes, SUM(s.total_logical_reads) AS leituras_total, SUM(s.total_logical_reads) * 1.0 / NULLIF(MAX(s.execution_count), 0) AS leituras_por_execucao,
       MAX(s.max_logical_reads) AS leituras_max
FROM sys.dm_exec_query_stats AS s CROSS APPLY sys.dm_exec_sql_text(s.sql_handle) AS t
WHERE t.text LIKE '%IX_PedidoMusica_Playout%' OR t.text LIKE '%Sorteio >= @r%' OR t.text LIKE '%Sorteio < @r%' OR t.text LIKE '%SELECT a.Bucket, a.ChaveStorage%'
GROUP BY CASE WHEN t.text LIKE '%IX_PedidoMusica_Playout%' THEN 'pedido' WHEN t.text LIKE '%Sorteio >= @r%' THEN 'R1 (>= @r)' WHEN t.text LIKE '%Sorteio < @r%' THEN 'R2 (< @r)'
              WHEN t.text LIKE '%SELECT a.Bucket, a.ChaveStorage%' THEN 'carga pela PK' END;
GO
PRINT '### R5 pior caso: 300 comerciais ativos, todos dentro da janela de 20 min: R1, R2 e R3 em sequência';
BEGIN TRAN;
;WITH x AS (SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) AS n FROM midia.ArquivoMidia WHERE TipoMidia = 2 AND StatusSanitizacao = 4 AND Ativo = 1)
UPDATE a SET Ativo = 0 FROM midia.ArquivoMidia a JOIN x ON x.Id = a.Id WHERE x.n > 300;
INSERT midia.Reproducao (MidiaId, IniciadoEmUtc) SELECT Id, DATEADD(MINUTE, -5, SYSUTCDATETIME()) FROM midia.ArquivoMidia WHERE TipoMidia = 2 AND StatusSanitizacao = 4 AND Ativo = 1;
DECLARE @r int = CHECKSUM(NEWID()), @cj datetime2(0) = DATEADD(MINUTE, -20, SYSUTCDATETIME());
SET STATISTICS IO ON;
EXEC sp_executesql N'SELECT TOP (1) a.Id, a.Titulo, a.Artista
FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio >= @r AND a.Id <> @emVoo
  AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo))
                  WHERE r.IniciadoEmUtc >= @corteJanela AND r.MidiaId = a.Id)
ORDER BY a.Sorteio;', N'@tipo tinyint, @r int, @corteJanela datetime2(0), @emVoo uniqueidentifier', @tipo = 2, @r = @r, @corteJanela = @cj, @emVoo = '00000000-0000-0000-0000-000000000000';
EXEC sp_executesql N'SELECT TOP (1) a.Id, a.Titulo, a.Artista
FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio < @r AND a.Id <> @emVoo
  AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo))
                  WHERE r.IniciadoEmUtc >= @corteJanela AND r.MidiaId = a.Id)
ORDER BY a.Sorteio;', N'@tipo tinyint, @r int, @corteJanela datetime2(0), @emVoo uniqueidentifier', @tipo = 2, @r = @r, @corteJanela = @cj, @emVoo = '00000000-0000-0000-0000-000000000000';
EXEC sp_executesql N'SELECT TOP (1) a.Id, a.Titulo, a.Artista
FROM (SELECT r.MidiaId, MAX(r.IniciadoEmUtc) AS Ultima
      FROM midia.Reproducao AS r WHERE r.IniciadoEmUtc >= @corteJanela GROUP BY r.MidiaId) AS u
JOIN midia.ArquivoMidia AS a ON a.Id = u.MidiaId
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Id <> @emVoo
ORDER BY u.Ultima, a.Id;', N'@tipo tinyint, @r int, @corteJanela datetime2(0), @emVoo uniqueidentifier', @tipo = 2, @r = @r, @corteJanela = @cj, @emVoo = '00000000-0000-0000-0000-000000000000';
SET STATISTICS IO OFF;
ROLLBACK;
GO
