/* =====================================================================
   WebRadio — infra/perf/medir-a01.sql
   Autor: Atlas (DBA) · tarefa E3-A01 · 2026-10-29

   Mede as consultas do C1 (pedidos) no dataset de gerar-dataset-a01.sql (cenário A ou B, -v CENARIO=). Para cada
   cenário: plano REAL (SET STATISTICS XML) e SET STATISTICS IO. Cada marcador "### " abre um cenário; o
   resumir-plano.py quebra a saída do sqlcmd por esse marcador.

   Texto das consultas (forge/e3-c1-pedidos, 8b635c0):
     Q1  ListarPedidosHandler.FilaDePendentes + PendentesAsync (ConsultasDePedido.cs): SQL do EF Core no formato do
         ToQueryString (Status = CAST(1 AS tinyint), projeção só do INCLUDE, ORDER BY CriadoEmUtc, Id, OFFSET/FETCH), mais o
         CountAsync da mesma fila, que o handler roda a cada GET /pedidos.
     Q2  MeusPedidosHandler: entidade inteira, TOP(@p) ORDER BY CriadoEmUtc DESC, por dispositivo (binary(32), o tipo da
         coluna) e por usuário.
     Q3  ExpiracaoDePedidos.Sql (Trabalhos/): UPDATE TOP (@lote) ... OUTPUT INTO + INSERT no outbox. O lote é medido dentro de
         uma transação que termina em ROLLBACK, com a contagem de locks; o laço inteiro (3z) vem por último e GRAVA.
     Q4  ConsultasDePedidos.EsperaDoFallbackAsync (Infrastructure/Pedidos): dispositivo, usuário e IP. O byte[] do
         SqlQuery interpolado vai como varbinary(8000) (suposição a confirmar com o Forge; o 4h usa binary(32) como controle).
     Q5  divulgacoes/ativas: texto PROVISÓRIO tirado do 07 (E3-F05), até o C2 definir o SQL.
     Q6  ListarPedidosHandler.ListaDeAprovados + AprovadosAsync (corte de 24 h em ModeradoEmUtc), mais o CountAsync.
     Q7  ConsultasDePedidos.TetoDePendentesAtingidoAsync: COUNT sobre TOP (@teto).
     Q9  ConsultasDePedidos.ProgramaDaJanelaAsync: o seek de faixa no IX_Programa_Grade, sem a credencial no meio.

   Ordem: leituras (Q1, Q2, Q6, Q7, Q9, Q5), preparo do fallback (grava), Q4, Q3 com ROLLBACK e, por último, o laço
   inteiro da expiração (3z, grava). Cenários frios: CHECKPOINT + DROPCLEANBUFFERS + FREEPROCCACHE antes de cada um.
   Só num banco descartável (a trava abaixo).

   Uso:  sqlcmd -C -S <host>,<porta> -U sa -b -I -y 0 -d WebRadioPerf -v CENARIO=A -i infra/perf/medir-a01.sql > saida.txt
         python3 infra/perf/resumir-plano.py saida.txt
   ===================================================================== */
:on error exit
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
IF DB_NAME() NOT LIKE N'%Perf%' THROW 50000, N'Banco não descartável: o nome precisa conter "Perf".', 1;
GO

PRINT '### 0 cenário $(CENARIO) e tamanho das estruturas';
SELECT i.name AS indice, i.has_filter, ps.used_page_count AS paginas, ps.row_count AS linhas
FROM sys.dm_db_partition_stats ps JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
WHERE ps.object_id IN (OBJECT_ID('interacao.PedidoMusica'), OBJECT_ID('interacao.Divulgacao')) ORDER BY ps.object_id, i.index_id;
GO

-- Valores que os cenários usam. Escolhidos aqui, fora do que é medido.
SELECT
  dev_top    = (SELECT TOP (1) ListenerDeviceHash FROM interacao.PedidoMusica WHERE ListenerDeviceHash IS NOT NULL
                GROUP BY ListenerDeviceHash ORDER BY COUNT(*) DESC, ListenerDeviceHash),
  dev_um     = (SELECT TOP (1) ListenerDeviceHash FROM interacao.PedidoMusica WHERE ListenerDeviceHash IS NOT NULL
                GROUP BY ListenerDeviceHash HAVING COUNT(*) = 1 ORDER BY ListenerDeviceHash),
  dev_nenhum = HASHBYTES('SHA2_256', N'dev-inexistente'),
  usr_top    = (SELECT TOP (1) UsuarioId FROM interacao.PedidoMusica WHERE UsuarioId IS NOT NULL
                GROUP BY UsuarioId ORDER BY COUNT(*) DESC, UsuarioId),
  usr_nenhum = NEWID(),
  ip_quente  = HASHBYTES('SHA2_256', N'ip-hot'),
  ip_comum   = HASHBYTES('SHA2_256', N'ip-1')
INTO #v;
SELECT dispositivos_do_top = (SELECT COUNT(*) FROM interacao.PedidoMusica p, #v WHERE p.ListenerDeviceHash = #v.dev_top),
       pedidos_do_usuario_top = (SELECT COUNT(*) FROM interacao.PedidoMusica p, #v WHERE p.UsuarioId = #v.usr_top);
GO

/* =============== Q1: fila de moderação (Status = 1) =============== */
DECLARE @sqlFila nvarchar(max) = N'SELECT [p].[Id], [p].[NomeOuvinte], [p].[TituloMusica], [p].[Artista], [p].[Mensagem], [p].[CriadoEmUtc], [p].[ProgramaId]
FROM [interacao].[PedidoMusica] AS [p]
WHERE [p].[Status] = CAST(1 AS tinyint)
ORDER BY [p].[CriadoEmUtc], [p].[Id]
OFFSET @p ROWS FETCH NEXT @p0 ROWS ONLY';
DECLARE @sqlFilaTotal nvarchar(max) = N'SELECT COUNT(*) FROM [interacao].[PedidoMusica] AS [p] WHERE [p].[Status] = CAST(1 AS tinyint)';
SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### 1a fila, primeira página (20)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlFila, N'@p int, @p0 int', @p = 0, @p0 = 20;
PRINT '### 1b fila, COUNT(*) do total (o CountAsync que o handler faz em toda chamada)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlFilaTotal;
PRINT '### 1c fila, página funda (tamanho 100, página 250: OFFSET 24.900)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlFila, N'@p int, @p0 int', @p = 24900, @p0 = 100;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
GO

/* =============== Q2: meus pedidos =============== */
DECLARE @sqlMeusDisp nvarchar(max) = N'SELECT TOP(@p) [p].[Id], [p].[ProgramaId], [p].[UsuarioId], [p].[NomeOuvinte], [p].[TituloMusica], [p].[Artista], [p].[Mensagem], [p].[ListenerDeviceHash], [p].[ListenerIpHash], [p].[Status], [p].[MidiaId], [p].[ModeradoPorUsuarioId], [p].[ModeradoEmUtc], [p].[MotivoRejeicao], [p].[EnviadoPlayoutEmUtc], [p].[TocadoEmUtc], [p].[CriadoEmUtc], [p].[RowVersion]
FROM [interacao].[PedidoMusica] AS [p]
WHERE [p].[ListenerDeviceHash] = @dispositivo
ORDER BY [p].[CriadoEmUtc] DESC';
DECLARE @sqlMeusUsr nvarchar(max) = N'SELECT TOP(@p) [p].[Id], [p].[ProgramaId], [p].[UsuarioId], [p].[NomeOuvinte], [p].[TituloMusica], [p].[Artista], [p].[Mensagem], [p].[ListenerDeviceHash], [p].[ListenerIpHash], [p].[Status], [p].[MidiaId], [p].[ModeradoPorUsuarioId], [p].[ModeradoEmUtc], [p].[MotivoRejeicao], [p].[EnviadoPlayoutEmUtc], [p].[TocadoEmUtc], [p].[CriadoEmUtc], [p].[RowVersion]
FROM [interacao].[PedidoMusica] AS [p]
WHERE [p].[UsuarioId] = @usuario
ORDER BY [p].[CriadoEmUtc] DESC';
DECLARE @dtop binary(32), @dum binary(32), @dnen binary(32), @utop uniqueidentifier, @unen uniqueidentifier;
SELECT @dtop = dev_top, @dum = dev_um, @dnen = dev_nenhum, @utop = usr_top, @unen = usr_nenhum FROM #v;
SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### 2a meus pedidos: dispositivo mais ativo (devolve 20 de muitos)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlMeusDisp, N'@p int, @dispositivo binary(32)', @p = 20, @dispositivo = @dtop;
PRINT '### 2b meus pedidos: dispositivo com um pedido só';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlMeusDisp, N'@p int, @dispositivo binary(32)', @p = 20, @dispositivo = @dum;
PRINT '### 2c meus pedidos: dispositivo desconhecido';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlMeusDisp, N'@p int, @dispositivo binary(32)', @p = 20, @dispositivo = @dnen;
PRINT '### 2d meus pedidos: usuário logado com mais pedidos';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlMeusUsr, N'@p int, @usuario uniqueidentifier', @p = 20, @usuario = @utop;
PRINT '### 2e meus pedidos: usuário sem pedidos';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlMeusUsr, N'@p int, @usuario uniqueidentifier', @p = 20, @usuario = @unen;
PRINT '### 2f reuso do plano de 2c (dispositivo desconhecido) para o dispositivo mais ativo';
EXEC sp_executesql @sqlMeusDisp, N'@p int, @dispositivo binary(32)', @p = 20, @dispositivo = @dtop;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
GO

/* =============== Q6: lista de Aprovado das últimas 24 h =============== */
DECLARE @sqlAprov nvarchar(max) = N'SELECT [p].[Id], [p].[NomeOuvinte], [p].[TituloMusica], [p].[Artista], [p].[Mensagem], [p].[CriadoEmUtc], [p].[ModeradoEmUtc], [p].[ProgramaId]
FROM [interacao].[PedidoMusica] AS [p]
WHERE [p].[Status] = CAST(2 AS tinyint) AND [p].[ModeradoEmUtc] >= @corte
ORDER BY [p].[ModeradoEmUtc], [p].[Id]
OFFSET @p ROWS FETCH NEXT @p0 ROWS ONLY';
DECLARE @sqlAprovTotal nvarchar(max) = N'SELECT COUNT(*) FROM [interacao].[PedidoMusica] AS [p] WHERE [p].[Status] = CAST(2 AS tinyint) AND [p].[ModeradoEmUtc] >= @corte';
-- Controle negativo: a mesma lista forçada ao clustered (o que seria sem o IX_PedidoMusica_Aprovados). Não é o código da aplicação.
DECLARE @sqlAprovSem nvarchar(max) = REPLACE(@sqlAprov, N'AS [p]', N'AS [p] WITH (INDEX(1))');
DECLARE @corte datetime2(3) = DATEADD(HOUR, -24, SYSUTCDATETIME());
SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### 6a aprovados 24 h, primeira página (20)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlAprov, N'@corte datetime2(3), @p int, @p0 int', @corte = @corte, @p = 0, @p0 = 20;
PRINT '### 6b aprovados 24 h, COUNT(*) do total';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlAprovTotal, N'@corte datetime2(3)', @corte = @corte;
PRINT '### 6c controle: aprovados 24 h forçados ao clustered (sem o índice novo)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlAprovSem, N'@corte datetime2(3), @p int, @p0 int', @corte = @corte, @p = 0, @p0 = 20;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
GO

/* =============== Q7: teto global de Pendente =============== */
DECLARE @sqlTeto nvarchar(max) = N'SELECT COUNT(*) AS [Value]
FROM (SELECT TOP (@p0) 1 AS X FROM interacao.PedidoMusica WHERE Status = 1) AS t';
SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### 7a teto global: COUNT sobre TOP (300)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlTeto, N'@p0 int', @p0 = 300;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
GO

/* =============== Q9: ProgramaId da janela (E3-D1) =============== */
DECLARE @sqlJanela nvarchar(max) = N'SELECT TOP (1) p.Id AS [Value]
            FROM grade.Programa AS p
            WHERE p.InicioUtc <= SYSUTCDATETIME()
              AND p.InicioUtc > DATEADD(MINUTE, -@p0, SYSUTCDATETIME())
              AND p.FimUtc > SYSUTCDATETIME()
              AND p.Status NOT IN (3, 4)
            ORDER BY p.InicioUtc DESC';
-- O AoVivo do dataset do A02 foi gravado na hora da carga; aqui ele cobre "agora" de verdade (como na Q3 do medir-a02.sql).
UPDATE grade.Programa SET InicioUtc = DATEADD(MINUTE, -20, CONVERT(datetime2(0), SYSUTCDATETIME())),
                          FimUtc = DATEADD(MINUTE, 40, CONVERT(datetime2(0), SYSUTCDATETIME())) WHERE Status = 2;
SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### 9a programa da janela, com programa em curso';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlJanela, N'@p0 int', @p0 = 720;
PRINT '### 9b programa da janela, sem programa em curso (o pedido fica sem ProgramaId)';
UPDATE grade.Programa SET InicioUtc = DATEADD(HOUR, 5, CONVERT(datetime2(0), SYSUTCDATETIME())), FimUtc = DATEADD(HOUR, 6, CONVERT(datetime2(0), SYSUTCDATETIME())), Status = 1 WHERE Status = 2;
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlJanela, N'@p0 int', @p0 = 720;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
GO

/* =============== Q5: divulgações ativas (texto provisório do 07, E3-F05) =============== */
DECLARE @sqlAtivas nvarchar(max) = N'SELECT [d].[Id], [d].[Titulo], [d].[Mensagem], [d].[ImagemChaveStorage], [d].[LinkDestino], [d].[Prioridade], [d].[InicioExibicaoUtc], [d].[FimExibicaoUtc]
FROM [interacao].[Divulgacao] AS [d]
WHERE [d].[Ativo] = CAST(1 AS bit) AND ([d].[InicioExibicaoUtc] IS NULL OR [d].[InicioExibicaoUtc] <= SYSUTCDATETIME()) AND ([d].[FimExibicaoUtc] IS NULL OR [d].[FimExibicaoUtc] > SYSUTCDATETIME())
ORDER BY [d].[Prioridade] DESC, [d].[Id]';
SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### 5a divulgações ativas (provisório)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlAtivas;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
GO

/* =============== Q4: fallback do throttle =============== */
-- Preparo (grava, banco descartável): pedidos dos últimos minutos que o fallback tem de achar. Status 5 porque a consulta
-- não filtra por status e assim não mexe na fila nem na lista de aprovados.
DECLARE @agora datetime2(3) = SYSUTCDATETIME();
DECLARE @ouvinte uniqueidentifier = (SELECT TOP (1) Id FROM seg.Usuario WHERE Role = 3 ORDER BY Id DESC);
INSERT interacao.PedidoMusica (UsuarioId, NomeOuvinte, TituloMusica, Artista, ListenerDeviceHash, ListenerIpHash, Status, CriadoEmUtc)
SELECT @ouvinte, N'Preparo', N'Faixa', N'Artista', HASHBYTES('SHA2_256', N'dev-hit'), HASHBYTES('SHA2_256', N'ip-1'), 5, DATEADD(SECOND, -60, @agora)
UNION ALL SELECT NULL, N'Preparo', N'Faixa', N'Artista', HASHBYTES('SHA2_256', CONCAT(N'dev-quente-', g.value)), HASHBYTES('SHA2_256', N'ip-hot'), 5, DATEADD(SECOND, -30 * g.value, @agora)
FROM GENERATE_SERIES(1, 3) AS g;
SELECT @ouvinte AS usuario_do_preparo INTO #prep;
GO

DECLARE @sqlDisp nvarchar(max) = N'SELECT TOP (1) DATEDIFF_BIG(MILLISECOND, CriadoEmUtc, SYSUTCDATETIME()) AS [Value]
            FROM interacao.PedidoMusica
            WHERE ListenerDeviceHash = @p0 AND CriadoEmUtc > DATEADD(SECOND, -@p1, SYSUTCDATETIME())
            ORDER BY CriadoEmUtc DESC';
DECLARE @sqlUsr nvarchar(max) = N'SELECT TOP (1) DATEDIFF_BIG(MILLISECOND, CriadoEmUtc, SYSUTCDATETIME()) AS [Value]
                FROM interacao.PedidoMusica
                WHERE UsuarioId = @p0 AND CriadoEmUtc > DATEADD(SECOND, -@p1, SYSUTCDATETIME())
                ORDER BY CriadoEmUtc DESC';
DECLARE @sqlIp nvarchar(max) = N'SELECT TOP (@p0) DATEDIFF_BIG(MILLISECOND, CriadoEmUtc, SYSUTCDATETIME()) AS [Value]
            FROM interacao.PedidoMusica
            WHERE ListenerIpHash = @p1 AND CriadoEmUtc > DATEADD(SECOND, -@p2, SYSUTCDATETIME())
            ORDER BY CriadoEmUtc DESC';
DECLARE @dhit varbinary(8000) = HASHBYTES('SHA2_256', N'dev-hit'), @dtop varbinary(8000), @dnen varbinary(8000),
        @ihot varbinary(8000), @icom varbinary(8000), @uhit uniqueidentifier, @utop uniqueidentifier;
SELECT @dtop = dev_top, @dnen = dev_nenhum, @ihot = ip_quente, @icom = ip_comum, @utop = usr_top FROM #v;
SELECT @uhit = usuario_do_preparo FROM #prep;
DECLARE @dhit32 binary(32) = @dhit;
SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### 4a fallback dispositivo: tem pedido nos últimos 5 min (varbinary(8000))';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlDisp, N'@p0 varbinary(8000), @p1 int', @p0 = @dhit, @p1 = 300;
PRINT '### 4b fallback dispositivo: sem pedido na janela (dispositivo desconhecido)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlDisp, N'@p0 varbinary(8000), @p1 int', @p0 = @dnen, @p1 = 300;
PRINT '### 4c fallback dispositivo: o mais ativo, sem pedido na janela';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlDisp, N'@p0 varbinary(8000), @p1 int', @p0 = @dtop, @p1 = 300;
PRINT '### 4d fallback usuário: tem pedido nos últimos 5 min';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlUsr, N'@p0 uniqueidentifier, @p1 int', @p0 = @uhit, @p1 = 300;
PRINT '### 4e fallback usuário: o com mais pedidos, sem pedido na janela';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlUsr, N'@p0 uniqueidentifier, @p1 int', @p0 = @utop, @p1 = 300;
PRINT '### 4f fallback IP quente (milhares de pedidos em 7 dias, 3 na janela), limite 3';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlIp, N'@p0 int, @p1 varbinary(8000), @p2 int', @p0 = 3, @p1 = @ihot, @p2 = 300;
PRINT '### 4g fallback IP comum (1 pedido na janela), limite 3';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlIp, N'@p0 int, @p1 varbinary(8000), @p2 int', @p0 = 3, @p1 = @icom, @p2 = 300;
PRINT '### 4h controle: dispositivo com binary(32) (o tipo da coluna) em vez de varbinary(8000)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlDisp, N'@p0 binary(32), @p1 int', @p0 = @dhit32, @p1 = 300;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
GO

/* =============== Q3: expiração em lote =============== */
DECLARE @sqlExp nvarchar(max) = N'DECLARE @expirados TABLE (Id uniqueidentifier, Hash binary(32) NULL);
UPDATE TOP (@lote) interacao.PedidoMusica SET Status = 5
OUTPUT inserted.Id, inserted.ListenerDeviceHash INTO @expirados
WHERE Status = 1 AND CriadoEmUtc < DATEADD(MINUTE, -@expiracao, SYSUTCDATETIME());
INSERT infra.EventoOutbox (Tipo, Payload)
SELECT ''PedidoModerado'',
       JSON_OBJECT(''grupos'': CASE WHEN Hash IS NULL THEN JSON_ARRAY(''locutores'')
                                  ELSE JSON_ARRAY(''locutores'', ''listener:'' + CONVERT(varchar(64), Hash, 2)) END,
                   ''dados'': JSON_OBJECT(''pedidoId'': LOWER(CONVERT(varchar(36), Id)), ''status'': ''Expirado''))
FROM @expirados;
SELECT COUNT(*) AS [Value] FROM @expirados;';
PRINT '### 3a expiração: um lote de 1.000 (ROLLBACK) e os locks que ele segura';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
SET STATISTICS IO ON; SET STATISTICS XML ON;
BEGIN TRAN;
EXEC sp_executesql @sqlExp, N'@lote int, @expiracao int', @lote = 1000, @expiracao = 120;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
SELECT i.name AS indice, l.resource_type, l.request_mode, COUNT(*) AS locks
FROM sys.dm_tran_locks AS l
LEFT JOIN sys.partitions AS pa ON pa.hobt_id = l.resource_associated_entity_id AND l.resource_type IN ('KEY', 'PAGE', 'RID', 'HOBT')
LEFT JOIN sys.indexes AS i ON i.object_id = pa.object_id AND i.index_id = pa.index_id
WHERE l.request_session_id = @@SPID AND l.resource_database_id = DB_ID() AND l.resource_type <> 'DATABASE'
GROUP BY i.name, l.resource_type, l.request_mode ORDER BY locks DESC;
ROLLBACK;
GO

-- 3b: a proposta do Atlas para o mesmo lote: o mesmo UPDATE TOP, com hint no IX_PedidoMusica_Fila. Sem o hint o plano compilado com os
-- parâmetros varre o clustered inteiro (3a); uma CTE com ORDER BY CriadoEmUtc sozinha também não basta (testado). Não é o código da
-- aplicação (ainda): é o que a 3a mostra que precisa mudar. Mesmo ROLLBACK e mesma contagem de locks.
DECLARE @sqlExpV1 nvarchar(max) = N'DECLARE @expirados TABLE (Id uniqueidentifier, Hash binary(32) NULL);
UPDATE TOP (@lote) p SET Status = 5 OUTPUT inserted.Id, inserted.ListenerDeviceHash INTO @expirados
FROM interacao.PedidoMusica AS p WITH (INDEX(IX_PedidoMusica_Fila))
WHERE p.Status = 1 AND p.CriadoEmUtc < DATEADD(MINUTE, -@expiracao, SYSUTCDATETIME());
SELECT COUNT(*) AS [Value] FROM @expirados;';
PRINT '### 3b expiração (proposta): lote de 1.000 com hint no IX_PedidoMusica_Fila (ROLLBACK) e os locks';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
SET STATISTICS IO ON; SET STATISTICS XML ON;
BEGIN TRAN;
EXEC sp_executesql @sqlExpV1, N'@lote int, @expiracao int', @lote = 1000, @expiracao = 120;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
SELECT i.name AS indice, l.resource_type, l.request_mode, COUNT(*) AS locks
FROM sys.dm_tran_locks AS l
LEFT JOIN sys.partitions AS pa ON pa.hobt_id = l.resource_associated_entity_id AND l.resource_type IN ('KEY', 'PAGE', 'RID', 'HOBT')
LEFT JOIN sys.indexes AS i ON i.object_id = pa.object_id AND i.index_id = pa.index_id
WHERE l.request_session_id = @@SPID AND l.resource_database_id = DB_ID() AND l.resource_type <> 'DATABASE'
GROUP BY i.name, l.resource_type, l.request_mode ORDER BY locks DESC;
ROLLBACK;
GO

/* ---- 3z: o laço inteiro, como o Worker (GRAVA; por isso é o último) ---- */
PRINT '### 3z expiração: o laço inteiro em lotes de 1.000, uma transação por lote (grava)';
DECLARE @sqlExp nvarchar(max) = N'SET XACT_ABORT ON; BEGIN TRAN;
DECLARE @expirados TABLE (Id uniqueidentifier, Hash binary(32) NULL);
UPDATE TOP (@lote) interacao.PedidoMusica SET Status = 5
OUTPUT inserted.Id, inserted.ListenerDeviceHash INTO @expirados
WHERE Status = 1 AND CriadoEmUtc < DATEADD(MINUTE, -@expiracao, SYSUTCDATETIME());
INSERT infra.EventoOutbox (Tipo, Payload)
SELECT ''PedidoModerado'',
       JSON_OBJECT(''grupos'': CASE WHEN Hash IS NULL THEN JSON_ARRAY(''locutores'')
                                  ELSE JSON_ARRAY(''locutores'', ''listener:'' + CONVERT(varchar(64), Hash, 2)) END,
                   ''dados'': JSON_OBJECT(''pedidoId'': LOWER(CONVERT(varchar(36), Id)), ''status'': ''Expirado''))
FROM @expirados;
SELECT @n = COUNT(*) FROM @expirados;
COMMIT;';
DECLARE @n int, @lotes int = 0, @total int = 0, @t0 datetime2(3) = SYSUTCDATETIME();
DECLARE @promo0 bigint = (SELECT ISNULL(SUM(index_lock_promotion_count), 0) FROM sys.dm_db_index_operational_stats(DB_ID(), OBJECT_ID('interacao.PedidoMusica'), NULL, NULL));
DECLARE @log0 bigint = (SELECT used_log_space_in_bytes FROM sys.dm_db_log_space_usage);
EXEC sp_executesql @sqlExp, N'@lote int, @expiracao int, @n int OUTPUT', @lote = 1000, @expiracao = 120, @n = @n OUTPUT;
SET @lotes += 1; SET @total += @n;
WHILE @n = 1000
BEGIN
    EXEC sp_executesql @sqlExp, N'@lote int, @expiracao int, @n int OUTPUT', @lote = 1000, @expiracao = 120, @n = @n OUTPUT;
    SET @lotes += 1; SET @total += @n;
END;
SELECT lotes = @lotes, expirados = @total, ms = DATEDIFF(MILLISECOND, @t0, SYSUTCDATETIME()),
       promocoes_de_lock = (SELECT ISNULL(SUM(index_lock_promotion_count), 0) FROM sys.dm_db_index_operational_stats(DB_ID(), OBJECT_ID('interacao.PedidoMusica'), NULL, NULL)) - @promo0,
       log_usado_kb = ((SELECT used_log_space_in_bytes FROM sys.dm_db_log_space_usage) - @log0) / 1024,
       eventos_no_outbox = (SELECT COUNT(*) FROM infra.EventoOutbox);
GO
