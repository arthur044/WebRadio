/* =====================================================================
   WebRadio — infra/perf/simular-rotacao-e4b.sql
   Autor: Atlas (DBA) · tarefa E4-A01b (08, E4-D4) · 2026-11-23

   Simula 100.000 sorteios da rotação com a JANELA DE REPETIÇÃO ATIVA, com as mesmas consultas do FilaDoPlayout.cs (R1 `Sorteio >= @r`, R2
   `Sorteio < @r`, R3 a mais antiga da janela) e a faixa em voo excluída, em bibliotecas de 100, 300 e 1.000 músicas. Roda num banco só com o schema do 03
   (o rodar-e4-a01b.sh cria o WebRadioSim). Relógio simulado: cada sorteio avança 200 s (uma faixa) e grava a Reproducao; janela de 90 min (o padrão da música),
   ou seja, ~27 faixas excluídas a cada sorteio.
   Duas variantes: K = 1 (o que o 08 especifica: a primeira candidata a partir de @r) e K = 5 (sorteia uma entre as 5 primeiras candidatas a partir de @r).
   Para cada cenário registra a distribuição de quantas vezes cada faixa saiu: razão máx/média, p99/média, mín/média e quantas nunca saíram; e, ao lado, a
   distribuição EXATA do mesmo critério sem janela (a probabilidade de cada faixa é o espaço da chave que a antecede / (K · 2^32)), que vale também para
   a biblioteca de 50.000 (onde 100 mil sorteios dariam ~2 por faixa e o ruído taparia o viés).
   Sem seg.Usuario: as FKs ficam em NOCHECK.
   ===================================================================== */
SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON;
ALTER TABLE midia.ArquivoMidia NOCHECK CONSTRAINT ALL;
ALTER TABLE midia.Reproducao NOCHECK CONSTRAINT ALL;
GO
CREATE TABLE dbo.Resultado (
    n int, k int, sorteios int, janela bit, faixas int, media float, minimo int, p01 float, p99 float, maximo int, razao_max_media float, razao_p99_media float,
    razao_min_media float, nunca_saiu int, usou_r3 int, segundos int);
GO
CREATE OR ALTER PROCEDURE dbo.usp_biblioteca @n int AS
BEGIN
    SET NOCOUNT ON;
    DELETE midia.Reproducao; DELETE midia.ArquivoMidia;
    DECLARE @u uniqueidentifier = NEWID();
    ;WITH x AS (SELECT TOP (@n) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b)
    INSERT midia.ArquivoMidia (NomeOriginal, Titulo, Artista, TipoMidia, Bucket, ChaveStorage, MimeTypeDeclarado, MimeType, TamanhoBytes, HashSHA256, DuracaoSegundos,
        StatusSanitizacao, EnviadoPorUsuarioId, UploadExpiraEmUtc, SanitizadoEmUtc, Ativo)
    SELECT CONCAT(N'm', i, N'.mp3'), CONCAT(N'Faixa ', i), N'Artista', 3, 'midia', CONCAT('s/', i, '.mp3'), 'audio/mpeg', 'audio/mpeg', 5000000,
           CAST(HASHBYTES('SHA2_256', CAST(i AS varchar(12))) AS binary(32)), 200, 4, @u, SYSUTCDATETIME(), SYSUTCDATETIME(), 1
    FROM x;
    UPDATE STATISTICS midia.ArquivoMidia WITH FULLSCAN;
END
GO
-- Distribuição exata (sem janela) do critério "uma entre as K primeiras candidatas a partir de @r", para a biblioteca carregada.
CREATE OR ALTER PROCEDURE dbo.usp_exata @k int AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @n int = (SELECT COUNT(*) FROM midia.ArquivoMidia);
    SELECT CAST(Sorteio AS bigint) AS v, ROW_NUMBER() OVER (ORDER BY Sorteio) AS rn INTO #s FROM midia.ArquivoMidia;
    CREATE UNIQUE CLUSTERED INDEX ix ON #s (rn);
    SELECT a.rn, (a.v - b.v + CASE WHEN b.rn >= a.rn THEN 4294967296 ELSE 0 END) / (@k * 4294967296.0) AS prob INTO #p
    FROM #s AS a JOIN #s AS b ON b.rn = ((a.rn - 1 - @k) % @n + @n) % @n + 1;
    ;WITH d AS (SELECT prob, NTILE(100) OVER (ORDER BY prob) AS pct FROM #p)
    SELECT @n AS faixas, @k AS k, MAX(prob) * @n AS razao_max_media, MIN(prob) * @n AS razao_min_media,
           (SELECT MAX(prob) FROM d WHERE pct <= 99) * @n AS razao_p99_media
    FROM #p;
    DROP TABLE #s, #p;
END
GO
CREATE OR ALTER PROCEDURE dbo.usp_simular @k int, @sorteios int AS
BEGIN
    SET NOCOUNT ON;
    DELETE midia.Reproducao;
    DECLARE @n int = (SELECT COUNT(*) FROM midia.ArquivoMidia), @ini datetime2(0) = SYSUTCDATETIME();
    CREATE TABLE #cont (Id uniqueidentifier NOT NULL PRIMARY KEY, n int NOT NULL);
    INSERT #cont SELECT Id, 0 FROM midia.ArquivoMidia;
    DECLARE @agora datetime2(0) = '2026-01-01T00:00:00', @cj datetime2(0), @i int = 0, @r int, @id uniqueidentifier, @emVoo uniqueidentifier = '00000000-0000-0000-0000-000000000000',
            @m int, @r3 int = 0, @falta int, @pick int;
    DECLARE @c TABLE (Id uniqueidentifier NOT NULL, ord int NOT NULL IDENTITY(1, 1));
    WHILE @i < @sorteios
    BEGIN
        SET @i += 1; SET @agora = DATEADD(SECOND, 200, @agora); SET @cj = DATEADD(MINUTE, -90, @agora); SET @r = CONVERT(int, CRYPT_GEN_RANDOM(4)); SET @id = NULL;
        DELETE @c;
        INSERT @c (Id)
        SELECT TOP (@k) a.Id FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
        WHERE a.TipoMidia = 3 AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio >= @r AND a.Id <> @emVoo
          AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo)) WHERE r.IniciadoEmUtc >= @cj AND r.MidiaId = a.Id)
        ORDER BY a.Sorteio;
        SET @falta = @k - (SELECT COUNT(*) FROM @c);
        IF @falta > 0
            INSERT @c (Id)
            SELECT TOP (@falta) a.Id FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
            WHERE a.TipoMidia = 3 AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio < @r AND a.Id <> @emVoo
              AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo)) WHERE r.IniciadoEmUtc >= @cj AND r.MidiaId = a.Id)
            ORDER BY a.Sorteio;
        SET @m = (SELECT COUNT(*) FROM @c);
        IF @m = 0
        BEGIN
            SET @r3 += 1;
            SELECT TOP (1) @id = a.Id
            FROM (SELECT r.MidiaId, MAX(r.IniciadoEmUtc) AS Ultima FROM midia.Reproducao AS r WHERE r.IniciadoEmUtc >= @cj GROUP BY r.MidiaId) AS u
            JOIN midia.ArquivoMidia AS a ON a.Id = u.MidiaId
            WHERE a.TipoMidia = 3 AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Id <> @emVoo ORDER BY u.Ultima, a.Id;
        END
        ELSE
        BEGIN
            SET @pick = 1 + ABS(CONVERT(int, CRYPT_GEN_RANDOM(4)) % @m);   -- ABS(int mínimo) estoura: o módulo vem antes
            SELECT @id = Id FROM @c WHERE ord = (SELECT MIN(ord) FROM @c) + @pick - 1;
        END
        IF @id IS NULL BREAK;
        INSERT midia.Reproducao (MidiaId, IniciadoEmUtc) VALUES (@id, @agora);
        UPDATE #cont SET n = n + 1 WHERE Id = @id;
        SET @emVoo = @id;
    END
    ;WITH d AS (SELECT n, NTILE(100) OVER (ORDER BY n) AS pct FROM #cont)
    INSERT dbo.Resultado
    SELECT @n, @k, @i, 1, @n, AVG(n * 1.0), MIN(n), NULL, NULL, MAX(n), MAX(n) / AVG(n * 1.0),
           (SELECT MAX(n) FROM d WHERE pct <= 99) / AVG(n * 1.0), MIN(n) / AVG(n * 1.0), SUM(CASE WHEN n = 0 THEN 1 ELSE 0 END), @r3,
           DATEDIFF(SECOND, @ini, SYSUTCDATETIME())
    FROM #cont;
    DROP TABLE #cont;
END
GO
PRINT '=== distribuição EXATA do critério, SEM janela (a probabilidade de cada faixa é o espaço da chave que a antecede) ===';
PRINT 'faixas, k, razão máx/média, razão mín/média, razão p99/média';
DECLARE @n int, @k int;
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT n, k FROM (VALUES (100, 1), (100, 5), (300, 1), (300, 5), (1000, 1), (1000, 5), (50000, 1), (50000, 5)) AS v(n, k);
OPEN c; FETCH NEXT FROM c INTO @n, @k;
WHILE @@FETCH_STATUS = 0
BEGIN
    EXEC dbo.usp_biblioteca @n;
    EXEC dbo.usp_exata @k;
    FETCH NEXT FROM c INTO @n, @k;
END
CLOSE c; DEALLOCATE c;
GO
PRINT '=== simulação de 100.000 sorteios COM a janela de 90 min ativa (200 s por faixa) ===';
DECLARE @n int, @k int;
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT n, k FROM (VALUES (100, 1), (100, 5), (300, 1), (300, 5), (1000, 1), (1000, 5)) AS v(n, k);
OPEN c; FETCH NEXT FROM c INTO @n, @k;
WHILE @@FETCH_STATUS = 0
BEGIN
    EXEC dbo.usp_biblioteca @n;
    EXEC dbo.usp_simular @k, 100000;
    FETCH NEXT FROM c INTO @n, @k;
END
CLOSE c; DEALLOCATE c;
SELECT n AS faixas, k, sorteios, CAST(media AS decimal(9,1)) AS media_por_faixa, minimo, maximo, CAST(razao_max_media AS decimal(5,2)) AS max_sobre_media,
       CAST(razao_p99_media AS decimal(5,2)) AS p99_sobre_media, CAST(razao_min_media AS decimal(5,2)) AS min_sobre_media, nunca_saiu, usou_r3 AS vezes_na_busca_3, segundos
FROM dbo.Resultado ORDER BY n, k;
GO
