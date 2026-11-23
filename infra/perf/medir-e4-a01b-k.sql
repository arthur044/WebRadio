/* =====================================================================
   WebRadio — infra/perf/medir-e4-a01b-k.sql
   Autor: Atlas (DBA) · tarefa E4-A01b (08, E4-D4) · 2026-11-23

   Custo em leituras lógicas da rotação com "uma entre as K primeiras candidatas a partir de @r" (a alternativa do 08 para o viés da chave fixa),
   K = 1, 5 e 10, no dataset do rodar-e4-a01b.sh (50.000 mídias, 2 milhões de Reproducao), com DUAS densidades de janela, ancoradas na última Reproducao
   (assim o resultado não depende de quanto tempo passou desde a carga): 90 min (324 linhas: o dataset do 08, ~228 faixas por hora, 4× mais denso que uma rádio de
   faixas de 200 s) e 7 min (~27 linhas: o que 90 min têm numa rádio de verdade). O NOT EXISTS da janela varre as linhas da janela a cada candidata, então o custo
   de cada K depende dessa densidade. 300 sorteios por K; as leituras vêm do
   dm_exec_query_stats (cada K é um plano diferente: TOP (@k) é parâmetro). O texto é o proposto ao Forge no parecer (seção 5): a busca `Sorteio >= @r` com
   TOP (@k) e, se vierem menos de K, o complemento `Sorteio < @r` com TOP (@k - já vindas); o sorteio entre as candidatas é da aplicação.
   ===================================================================== */
SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON;
GO
DECLARE @ks TABLE (k int, janela_min int); INSERT @ks VALUES (1, 7), (5, 7), (10, 7), (1, 90), (5, 90), (10, 90);
DECLARE @k int, @janela int, @i int, @r int, @cj datetime2(0), @falta int, @linhas int;
DECLARE @c TABLE (Id uniqueidentifier, Titulo nvarchar(200), Artista nvarchar(200));
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT k, janela_min FROM @ks;
OPEN cur; FETCH NEXT FROM cur INTO @k, @janela;
WHILE @@FETCH_STATUS = 0
BEGIN
    DBCC FREEPROCCACHE WITH NO_INFOMSGS;
    SET @cj = DATEADD(MINUTE, -@janela, (SELECT MAX(IniciadoEmUtc) FROM midia.Reproducao));
    SET @linhas = (SELECT COUNT(*) FROM midia.Reproducao WHERE IniciadoEmUtc >= @cj);
    SET @i = 0;
    WHILE @i < 300
    BEGIN
        SET @i += 1; SET @r = CHECKSUM(NEWID()); DELETE @c;
        INSERT @c EXEC sp_executesql N'SELECT TOP (@k) a.Id, a.Titulo, a.Artista
FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio >= @r AND a.Id <> @emVoo
  AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo)) WHERE r.IniciadoEmUtc >= @corteJanela AND r.MidiaId = a.Id)
ORDER BY a.Sorteio;', N'@k int, @tipo tinyint, @r int, @corteJanela datetime2(0), @emVoo uniqueidentifier', @k = @k, @tipo = 3, @r = @r, @corteJanela = @cj, @emVoo = '00000000-0000-0000-0000-000000000000';
        SET @falta = @k - (SELECT COUNT(*) FROM @c);
        IF @falta > 0
            INSERT @c EXEC sp_executesql N'SELECT TOP (@k) a.Id, a.Titulo, a.Artista
FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio < @r AND a.Id <> @emVoo
  AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo)) WHERE r.IniciadoEmUtc >= @corteJanela AND r.MidiaId = a.Id)
ORDER BY a.Sorteio;', N'@k int, @tipo tinyint, @r int, @corteJanela datetime2(0), @emVoo uniqueidentifier', @k = @falta, @tipo = 3, @r = @r, @corteJanela = @cj, @emVoo = '00000000-0000-0000-0000-000000000000';
    END
    SELECT @k AS k, @janela AS janela_min, @linhas AS linhas_na_janela,
           (SELECT SUM(s.total_logical_reads) * 1.0 / NULLIF(MAX(s.execution_count), 0) FROM sys.dm_exec_query_stats AS s CROSS APPLY sys.dm_exec_sql_text(s.sql_handle) AS t WHERE t.text LIKE '%Sorteio >= @r%') AS leituras_por_chamada_busca1,
           (SELECT MAX(s.max_logical_reads) FROM sys.dm_exec_query_stats AS s CROSS APPLY sys.dm_exec_sql_text(s.sql_handle) AS t WHERE t.text LIKE '%Sorteio >= @r%') AS leituras_max;
    FETCH NEXT FROM cur INTO @k, @janela;
END
CLOSE cur; DEALLOCATE cur;
GO
