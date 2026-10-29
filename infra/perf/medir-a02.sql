/* =====================================================================
   WebRadio — infra/perf/medir-a02.sql
   Autor: Atlas (DBA) · tarefa E2-A02 · 2026-10-20

   Mede as 3 consultas da grade no dataset de gerar-dataset-a02.sql. Para cada cenário:
   plano de execução REAL (SET STATISTICS XML) e SET STATISTICS IO. Cada marcador "### " abre um
   cenário; o resumir-plano.py quebra a saída do sqlcmd por esse marcador.

   Texto das consultas, igual ao que a aplicação manda:
     Q1  GradeDeProgramas.GarantirSemConflitoAsync (main, #40). SQL do EF Core 10 capturado com ToQueryString, tipos dos
         parâmetros conferidos no comando enviado: datetime2(7) contra colunas datetime2(0), tinyint, uniqueidentifier.
         Criação = sem o predicado do Id (o EF o elimina com ignorarId nulo); edição = com "p.Id <> @ignorarId".
     Q2  TransicoesDaGrade.Sql (main), com os valores do enum já interpolados. Os cenários de escrita rodam dentro de uma
         transação que termina em ROLLBACK: o dataset não muda. (A Q3 e a Q4 gravam o preparo do AoVivo, sem rollback.)
     Q3  ConsultasDoHarbor.JanelaAsync (main, desde o daae154): @p0 = identificador e @p1 = Programa.DuracaoMaxima
         em minutos (720), como o SqlQuery do EF.

   Ordem: Q1, Q3 e Q4 (leituras) antes da Q2 (escritas). O ROLLBACK dos cenários do Worker deixa registros fantasma e
   páginas divididas justamente na faixa de "agora" até a limpeza em segundo plano, e isso inflaria as leituras da janela.

   Cenários frios: CHECKPOINT + DBCC DROPCLEANBUFFERS + DBCC FREEPROCCACHE antes de cada um (plano compilado com os
   valores do próprio cenário). Só num banco descartável (a trava abaixo).

   Uso:  sqlcmd -C -S <host>,<porta> -U sa -b -I -y 0 -d WebRadioPerf -i infra/perf/medir-a02.sql > saida.txt
         python3 infra/perf/resumir-plano.py saida.txt
   ===================================================================== */
:on error exit
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
IF DB_NAME() NOT LIKE N'%Perf%' THROW 50000, N'Banco não descartável: o nome precisa conter "Perf".', 1;
GO

PRINT '### 0 tamanho das estruturas';
SELECT i.name AS indice, ps.used_page_count AS paginas, ps.row_count AS linhas
FROM sys.dm_db_partition_stats ps JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
WHERE ps.object_id = OBJECT_ID('grade.Programa') ORDER BY i.index_id;
SELECT i.name AS indice, i.has_filter, ps.used_page_count AS paginas, ps.row_count AS linhas
FROM sys.dm_db_partition_stats ps JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
WHERE ps.object_id = OBJECT_ID('grade.Programa') AND i.name = 'IX_Programa_Worker';
GO

/* =============== Q1: interseção da criação e da edição =============== */
DECLARE @sqlCriacao nvarchar(max) = N'SELECT TOP(@p) [p].[Id]
FROM [grade].[Programa] AS [p]
WHERE [p].[Status] IN (CAST(1 AS tinyint), CAST(2 AS tinyint)) AND [p].[InicioUtc] < @fimUtc AND [p].[InicioUtc] > @limite AND [p].[FimUtc] > @inicioUtc
ORDER BY [p].[InicioUtc]';
DECLARE @sqlEdicao nvarchar(max) = N'SELECT TOP(@p) [p].[Id]
FROM [grade].[Programa] AS [p]
WHERE [p].[Status] IN (CAST(1 AS tinyint), CAST(2 AS tinyint)) AND [p].[InicioUtc] < @fimUtc AND [p].[InicioUtc] > @limite AND [p].[FimUtc] > @inicioUtc AND [p].[Id] <> @ignorarId
ORDER BY [p].[InicioUtc]';
DECLARE @defC nvarchar(200) = N'@p int, @fimUtc datetime2(7), @limite datetime2(7), @inicioUtc datetime2(7)';
DECLARE @defE nvarchar(200) = N'@p int, @fimUtc datetime2(7), @limite datetime2(7), @inicioUtc datetime2(7), @ignorarId uniqueidentifier';

-- Amanhã 20:00-21:00 (grade futura densa: há conflito) e o Agendado dessa janela (para a edição).
DECLARE @i1 datetime2(7) = DATEADD(HOUR, 20, CONVERT(datetime2(7), CONVERT(date, DATEADD(DAY, 1, SYSUTCDATETIME()))));
DECLARE @f1 datetime2(7) = DATEADD(HOUR, 1, @i1), @l1 datetime2(7) = DATEADD(HOUR, -12, @i1);
-- Janela sem nenhum ativo na faixa de 12 h, no meio do histórico denso: toda linha da faixa é rejeitada pelo Status.
DECLARE @i2 datetime2(7) = '2024-03-10 03:00:00', @f2 datetime2(7) = '2024-03-10 03:30:00', @l2 datetime2(7) = DATEADD(HOUR, -12, '2024-03-10 03:00:00');
-- Daqui a 90 dias: além do último passo do histograma (a grade futura vai a 30 dias).
DECLARE @i3 datetime2(7) = DATEADD(HOUR, 20, CONVERT(datetime2(7), CONVERT(date, DATEADD(DAY, 90, SYSUTCDATETIME()))));
DECLARE @f3 datetime2(7) = DATEADD(HOUR, 1, @i3), @l3 datetime2(7) = DATEADD(HOUR, -12, @i3);
-- Edição que não muda a janela: o Agendado de amanhã 20:00 (exclui o próprio Id; os vizinhos encostam e não conflitam).
DECLARE @ed uniqueidentifier = (SELECT TOP (1) Id FROM grade.Programa WHERE Status = 1 AND InicioUtc >= @i1 ORDER BY InicioUtc);
DECLARE @i4 datetime2(7), @f4 datetime2(7), @l4 datetime2(7);
SELECT @i4 = InicioUtc, @f4 = FimUtc, @l4 = DATEADD(HOUR, -12, InicioUtc) FROM grade.Programa WHERE Id = @ed;

SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### 1a criação com conflito (amanhã 20:00-21:00)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlCriacao, @defC, @p = 1, @fimUtc = @f1, @limite = @l1, @inicioUtc = @i1;
PRINT '### 1b criação sem conflito, histórico denso (2024-03-10 03:00-03:30)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlCriacao, @defC, @p = 1, @fimUtc = @f2, @limite = @l2, @inicioUtc = @i2;
PRINT '### 1c criação sem conflito, além do histograma (+90 dias)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlCriacao, @defC, @p = 1, @fimUtc = @f3, @limite = @l3, @inicioUtc = @i3;
PRINT '### 1d edição sem mudar a janela (exclui o próprio Id)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlEdicao, @defE, @p = 1, @fimUtc = @f4, @limite = @l4, @inicioUtc = @i4, @ignorarId = @ed;
PRINT '### 1e edição com conflito (move o Agendado para cima de outro: amanhã 20:00-21:00)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlEdicao, @defE, @p = 1, @fimUtc = @f1, @limite = @l1, @inicioUtc = @i1, @ignorarId = @ed;

-- Reuso: plano compilado com a janela vazia de +90 dias, executado com as outras (sensibilidade ao parâmetro).
PRINT '### 1f reuso do plano de 1c, executado com 1a (conflito)';
DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlCriacao, @defC, @p = 1, @fimUtc = @f3, @limite = @l3, @inicioUtc = @i3;
EXEC sp_executesql @sqlCriacao, @defC, @p = 1, @fimUtc = @f1, @limite = @l1, @inicioUtc = @i1;
PRINT '### 1g reuso do plano de 1c, executado com 1b (histórico denso)';
EXEC sp_executesql @sqlCriacao, @defC, @p = 1, @fimUtc = @f2, @limite = @l2, @inicioUtc = @i2;
PRINT '### 1h reuso do plano de 1b, executado com 1a e 1c';
DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlCriacao, @defC, @p = 1, @fimUtc = @f2, @limite = @l2, @inicioUtc = @i2;
EXEC sp_executesql @sqlCriacao, @defC, @p = 1, @fimUtc = @f1, @limite = @l1, @inicioUtc = @i1;
EXEC sp_executesql @sqlCriacao, @defC, @p = 1, @fimUtc = @f3, @limite = @l3, @inicioUtc = @i3;
-- Controle negativo: a mesma interseção SEM o limite de 12 h (InicioUtc > @limite). Não é o código da aplicação.
DECLARE @sqlSemLimite nvarchar(max) = N'SELECT TOP(@p) [p].[Id]
FROM [grade].[Programa] AS [p]
WHERE [p].[Status] IN (CAST(1 AS tinyint), CAST(2 AS tinyint)) AND [p].[InicioUtc] < @fimUtc AND [p].[FimUtc] > @inicioUtc
ORDER BY [p].[InicioUtc]';
PRINT '### 1i controle: criação sem conflito, histórico denso, SEM o limite de 12 h';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlSemLimite, N'@p int, @fimUtc datetime2(7), @inicioUtc datetime2(7)', @p = 1, @fimUtc = @f2, @inicioUtc = @i2;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
GO

/* =============== Q3: janela do harbor/auth (ConsultasDoHarbor.JanelaAsync) =============== */
-- Preparo: o AoVivo do dataset foi gravado na hora da carga e já pode ter passado do FimUtc. Aqui ele cobre "agora" de
-- verdade, como num programa em curso. Fica gravado (banco descartável): medir dentro da transação aberta do preparo
-- soma leituras que não existem em produção.
UPDATE grade.Programa SET InicioUtc = DATEADD(MINUTE, -20, CONVERT(datetime2(0), SYSUTCDATETIME())),
                          FimUtc = DATEADD(MINUTE, 40, CONVERT(datetime2(0), SYSUTCDATETIME())) WHERE Status = 2;
GO
DECLARE @sql3 nvarchar(max) = N'SELECT TOP (1) p.Id AS ProgramaId, p.FimUtc
            FROM seg.CredencialTransmissao AS c
            JOIN seg.Usuario AS u ON u.Id = c.UsuarioId
            JOIN grade.Programa AS p ON p.LocutorId = c.UsuarioId
            WHERE c.Identificador = @p0
              AND c.RevogadaEmUtc IS NULL
              AND u.Ativo = 1
              AND u.Role IN (1, 2)
              AND p.InicioUtc <= SYSUTCDATETIME()
              AND p.InicioUtc > DATEADD(MINUTE, -@p1, SYSUTCDATETIME())
              AND p.FimUtc > SYSUTCDATETIME()
              AND p.Status NOT IN (3, 4)
            ORDER BY p.InicioUtc DESC';
-- Locutor do AoVivo (dentro da janela), locutor de um Agendado futuro que não está no ar, credencial revogada, inexistente.
DECLARE @dentro uniqueidentifier = (SELECT c.Identificador FROM grade.Programa p JOIN seg.CredencialTransmissao c ON c.UsuarioId = p.LocutorId AND c.RevogadaEmUtc IS NULL WHERE p.Status = 2);
DECLARE @fora uniqueidentifier = (SELECT TOP (1) c.Identificador FROM seg.CredencialTransmissao c WHERE c.RevogadaEmUtc IS NULL
                                  AND c.UsuarioId <> (SELECT LocutorId FROM grade.Programa WHERE Status = 2) ORDER BY c.Identificador);
DECLARE @revogada uniqueidentifier = (SELECT TOP (1) Identificador FROM seg.CredencialTransmissao WHERE RevogadaEmUtc IS NOT NULL ORDER BY Identificador);
DECLARE @inexistente uniqueidentifier = NEWID();

SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### 3a credencial do locutor no ar (dentro da janela)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sql3, N'@p0 uniqueidentifier, @p1 int', @p1 = 720, @p0 = @dentro;
PRINT '### 3b credencial ativa de locutor sem programa na janela (fora da janela)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sql3, N'@p0 uniqueidentifier, @p1 int', @p1 = 720, @p0 = @fora;
PRINT '### 3c credencial revogada';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sql3, N'@p0 uniqueidentifier, @p1 int', @p1 = 720, @p0 = @revogada;
PRINT '### 3d identificador inexistente';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sql3, N'@p0 uniqueidentifier, @p1 int', @p1 = 720, @p0 = @inexistente;
PRINT '### 3e reuso do plano de 3d (compilado com a credencial inexistente) para o locutor no ar e o fora da janela';
EXEC sp_executesql @sql3, N'@p0 uniqueidentifier, @p1 int', @p1 = 720, @p0 = @dentro;
EXEC sp_executesql @sql3, N'@p0 uniqueidentifier, @p1 int', @p1 = 720, @p0 = @fora;
-- Controle negativo: a janela SEM o limite de 12 h. Não é o código da aplicação.
DECLARE @sql3sem nvarchar(max) = REPLACE(@sql3, N'AND p.InicioUtc > DATEADD(MINUTE, -@p1, SYSUTCDATETIME())', N'');
PRINT '### 3f controle: locutor fora da janela, SEM o limite de 12 h';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sql3sem, N'@p0 uniqueidentifier, @p1 int', @p1 = 720, @p0 = @fora;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
GO

/* =============== Q4: vizinhas da grade (fora das 3 pedidas na E2-A02, mesma tabela e mesma rota quente) =============== */
-- Preparo (repete o da Q3, para a Q4 rodar sozinha): o AoVivo cobre "agora".
UPDATE grade.Programa SET InicioUtc = DATEADD(MINUTE, -20, CONVERT(datetime2(0), SYSUTCDATETIME())),
                          FimUtc = DATEADD(MINUTE, 40, CONVERT(datetime2(0), SYSUTCDATETIME())) WHERE Status = 2;
GO
DECLARE @sqlAoVivo nvarchar(max) = N'SELECT [p0].[Id], [p0].[AtualizadoEmUtc], [p0].[CanceladoEmUtc], [p0].[CriadoEmUtc], [p0].[Descricao], [p0].[FimUtc], [p0].[InicioUtc], [p0].[LocutorId], [p0].[RowVersion], [p0].[Status], [p0].[Titulo], [u].[Nome]
FROM (
    SELECT TOP(@p) [p].[Id], [p].[AtualizadoEmUtc], [p].[CanceladoEmUtc], [p].[CriadoEmUtc], [p].[Descricao], [p].[FimUtc], [p].[InicioUtc], [p].[LocutorId], [p].[RowVersion], [p].[Status], [p].[Titulo]
    FROM [grade].[Programa] AS [p]
    WHERE [p].[Status] = CAST(2 AS tinyint)
    ORDER BY [p].[InicioUtc]
) AS [p0]
INNER JOIN [seg].[Usuario] AS [u] ON [p0].[LocutorId] = [u].[Id]
ORDER BY [p0].[InicioUtc]';
DECLARE @sqlLista nvarchar(max) = N'SELECT [p].[Id], [p].[AtualizadoEmUtc], [p].[CanceladoEmUtc], [p].[CriadoEmUtc], [p].[Descricao], [p].[FimUtc], [p].[InicioUtc], [p].[LocutorId], [p].[RowVersion], [p].[Status], [p].[Titulo], [u].[Nome]
FROM [grade].[Programa] AS [p]
INNER JOIN [seg].[Usuario] AS [u] ON [p].[LocutorId] = [u].[Id]
WHERE [p].[InicioUtc] < @ateUtc AND [p].[InicioUtc] > @limite AND [p].[FimUtc] > @deUtc AND [p].[Status] <> CAST(4 AS tinyint)
ORDER BY [p].[InicioUtc]';
DECLARE @de datetime2(7) = CONVERT(datetime2(7), CONVERT(date, SYSUTCDATETIME()));
DECLARE @ate datetime2(7) = DATEADD(DAY, 7, @de), @lim datetime2(7) = DATEADD(HOUR, -12, @de);
SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### 4a GET /programas/ao-vivo';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlAoVivo, N'@p int', @p = 1;
PRINT '### 4b GET /programas (semana atual, sem cancelados)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlLista, N'@ateUtc datetime2(7), @limite datetime2(7), @deUtc datetime2(7)', @ateUtc = @ate, @limite = @lim, @deUtc = @de;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
GO

/* =============== Q2: UPDATE do Worker (TransicoesDaGrade.Sql) =============== */
-- 2a: o ciclo "com trabalho" e logo em seguida o ciclo "ocioso" (idempotência), na mesma transação.
-- Preparo: o primeiro Agendado já começou (janela em curso) e o AoVivo já passou do FimUtc.
BEGIN TRAN;
DECLARE @t datetime2(0) = CONVERT(datetime2(0), SYSUTCDATETIME());
UPDATE grade.Programa SET InicioUtc = DATEADD(SECOND, -120, @t), FimUtc = DATEADD(MINUTE, 10, @t)
WHERE Id = (SELECT TOP (1) Id FROM grade.Programa WHERE Status = 1 ORDER BY InicioUtc);
UPDATE grade.Programa SET InicioUtc = DATEADD(SECOND, -600, @t), FimUtc = DATEADD(SECOND, -60, @t) WHERE Status = 2;
GO
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### 2a Worker com trabalho: 1 Agendado vira AoVivo e 1 AoVivo vira Finalizado';
SET XACT_ABORT ON;
BEGIN TRAN;
DECLARE @agora datetime2(3) = SYSUTCDATETIME();
DECLARE @iniciados TABLE (Id uniqueidentifier, Titulo nvarchar(120), LocutorId uniqueidentifier);
DECLARE @finalizados TABLE (Id uniqueidentifier, Titulo nvarchar(120), LocutorId uniqueidentifier);

UPDATE grade.Programa SET Status = 2, AtualizadoEmUtc = @agora
OUTPUT inserted.Id, inserted.Titulo, inserted.LocutorId INTO @iniciados
WHERE Status = 1 AND InicioUtc <= @agora AND FimUtc > @agora;

UPDATE grade.Programa SET Status = 3, AtualizadoEmUtc = @agora
OUTPUT inserted.Id, inserted.Titulo, inserted.LocutorId INTO @finalizados
WHERE Status IN (1, 2) AND FimUtc <= @agora;

INSERT infra.EventoOutbox (Tipo, Payload)
SELECT 'ProgramaStatusAlterado',
       JSON_OBJECT('grupos': NULL,
                   'dados': JSON_OBJECT('programaId': LOWER(CONVERT(varchar(36), p.Id)), 'titulo': p.Titulo,
                                        'status': 'AoVivo', 'locutorNome': u.Nome))
FROM @iniciados p JOIN seg.Usuario u ON u.Id = p.LocutorId
UNION ALL
SELECT 'ProgramaStatusAlterado',
       JSON_OBJECT('grupos': NULL,
                   'dados': JSON_OBJECT('programaId': LOWER(CONVERT(varchar(36), p.Id)), 'titulo': p.Titulo,
                                        'status': 'Finalizado', 'locutorNome': u.Nome))
FROM @finalizados p JOIN seg.Usuario u ON u.Id = p.LocutorId;

SELECT (SELECT COUNT(*) FROM @iniciados) AS Iniciados, (SELECT COUNT(*) FROM @finalizados) AS Finalizados;
COMMIT;
GO
PRINT '### 2b Worker ocioso (segundo ciclo, nada a mudar: o caso de 99,9% dos ciclos de 10 s)';
BEGIN TRAN;
DECLARE @agora datetime2(3) = SYSUTCDATETIME();
DECLARE @iniciados TABLE (Id uniqueidentifier, Titulo nvarchar(120), LocutorId uniqueidentifier);
DECLARE @finalizados TABLE (Id uniqueidentifier, Titulo nvarchar(120), LocutorId uniqueidentifier);

UPDATE grade.Programa SET Status = 2, AtualizadoEmUtc = @agora
OUTPUT inserted.Id, inserted.Titulo, inserted.LocutorId INTO @iniciados
WHERE Status = 1 AND InicioUtc <= @agora AND FimUtc > @agora;

UPDATE grade.Programa SET Status = 3, AtualizadoEmUtc = @agora
OUTPUT inserted.Id, inserted.Titulo, inserted.LocutorId INTO @finalizados
WHERE Status IN (1, 2) AND FimUtc <= @agora;

INSERT infra.EventoOutbox (Tipo, Payload)
SELECT 'ProgramaStatusAlterado',
       JSON_OBJECT('grupos': NULL,
                   'dados': JSON_OBJECT('programaId': LOWER(CONVERT(varchar(36), p.Id)), 'titulo': p.Titulo,
                                        'status': 'AoVivo', 'locutorNome': u.Nome))
FROM @iniciados p JOIN seg.Usuario u ON u.Id = p.LocutorId
UNION ALL
SELECT 'ProgramaStatusAlterado',
       JSON_OBJECT('grupos': NULL,
                   'dados': JSON_OBJECT('programaId': LOWER(CONVERT(varchar(36), p.Id)), 'titulo': p.Titulo,
                                        'status': 'Finalizado', 'locutorNome': u.Nome))
FROM @finalizados p JOIN seg.Usuario u ON u.Id = p.LocutorId;

SELECT (SELECT COUNT(*) FROM @iniciados) AS Iniciados, (SELECT COUNT(*) FROM @finalizados) AS Finalizados;
COMMIT;
GO
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
ROLLBACK;
GO

-- 2c: Worker parado: 500 Agendados vencidos de uma vez (o Worker volta e recupera tudo no primeiro ciclo).
BEGIN TRAN;
DECLARE @t datetime2(0) = CONVERT(datetime2(0), SYSUTCDATETIME());
UPDATE grade.Programa SET InicioUtc = DATEADD(SECOND, -600, @t), FimUtc = DATEADD(SECOND, -60, @t)
WHERE Id IN (SELECT TOP (500) Id FROM grade.Programa WHERE Status = 1 ORDER BY InicioUtc);
GO
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### 2c Worker com backlog: 500 Agendados vencidos viram Finalizado';
SET XACT_ABORT ON;
BEGIN TRAN;
DECLARE @agora datetime2(3) = SYSUTCDATETIME();
DECLARE @iniciados TABLE (Id uniqueidentifier, Titulo nvarchar(120), LocutorId uniqueidentifier);
DECLARE @finalizados TABLE (Id uniqueidentifier, Titulo nvarchar(120), LocutorId uniqueidentifier);

UPDATE grade.Programa SET Status = 2, AtualizadoEmUtc = @agora
OUTPUT inserted.Id, inserted.Titulo, inserted.LocutorId INTO @iniciados
WHERE Status = 1 AND InicioUtc <= @agora AND FimUtc > @agora;

UPDATE grade.Programa SET Status = 3, AtualizadoEmUtc = @agora
OUTPUT inserted.Id, inserted.Titulo, inserted.LocutorId INTO @finalizados
WHERE Status IN (1, 2) AND FimUtc <= @agora;

INSERT infra.EventoOutbox (Tipo, Payload)
SELECT 'ProgramaStatusAlterado',
       JSON_OBJECT('grupos': NULL,
                   'dados': JSON_OBJECT('programaId': LOWER(CONVERT(varchar(36), p.Id)), 'titulo': p.Titulo,
                                        'status': 'AoVivo', 'locutorNome': u.Nome))
FROM @iniciados p JOIN seg.Usuario u ON u.Id = p.LocutorId
UNION ALL
SELECT 'ProgramaStatusAlterado',
       JSON_OBJECT('grupos': NULL,
                   'dados': JSON_OBJECT('programaId': LOWER(CONVERT(varchar(36), p.Id)), 'titulo': p.Titulo,
                                        'status': 'Finalizado', 'locutorNome': u.Nome))
FROM @finalizados p JOIN seg.Usuario u ON u.Id = p.LocutorId;

SELECT (SELECT COUNT(*) FROM @iniciados) AS Iniciados, (SELECT COUNT(*) FROM @finalizados) AS Finalizados;
COMMIT;
GO
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
ROLLBACK;
GO

-- 2d/2e (opcional, informativo): o mesmo 2º UPDATE com "AND InicioUtc < @agora". Redundante pelo CK_Programa_Intervalo
-- (FimUtc > InicioUtc), mas vira predicado de busca no IX_Programa_Worker (Status, InicioUtc). Medido no ciclo ocioso: o
-- que já venceu desde a carga é processado antes, na mesma transação.
BEGIN TRAN;
DECLARE @a datetime2(3) = SYSUTCDATETIME();
UPDATE grade.Programa SET Status = 2, AtualizadoEmUtc = @a WHERE Status = 1 AND InicioUtc <= @a AND FimUtc > @a;
UPDATE grade.Programa SET Status = 3, AtualizadoEmUtc = @a WHERE Status IN (1, 2) AND FimUtc <= @a;
GO
CHECKPOINT; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### 2d 2º UPDATE ocioso, como hoje (Status IN (1, 2) AND FimUtc <= @agora)';
DECLARE @agora datetime2(3) = SYSUTCDATETIME();
UPDATE grade.Programa SET Status = 3, AtualizadoEmUtc = @agora WHERE Status IN (1, 2) AND FimUtc <= @agora;
GO
PRINT '### 2e 2º UPDATE ocioso, variante (… AND InicioUtc < @agora)';
DECLARE @agora datetime2(3) = SYSUTCDATETIME();
UPDATE grade.Programa SET Status = 3, AtualizadoEmUtc = @agora WHERE Status IN (1, 2) AND InicioUtc < @agora AND FimUtc <= @agora;
GO
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
ROLLBACK;
GO

SET STATISTICS XML OFF; SET STATISTICS IO OFF;
GO
