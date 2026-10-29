/* =====================================================================
   WebRadio — infra/perf/medir-a01-retencao.sql
   Autor: Atlas (DBA) · tarefa E3-A02 · 2026-10-29

   Mede a retenção do E3-D8 (Manutencao.cs do forge/e3-c1-pedidos, PR #81) no dataset de gerar-dataset-a01.sql:
     R1  hash do IP aos 7 dias            R2  anonimização de 30 dias (um UPDATE com CASE nos terminais)
     R3  eventos de pedido do outbox (30 dias, entregues ou parados)    R4  a limpeza antiga do outbox (7 dias, entregues)
     R5  o índice candidato do A02: IX_PedidoMusica_Anonimizar (CriadoEmUtc) WHERE ListenerDeviceHash IS NOT NULL
     R6  a rotina inteira como o EmLotes do Worker (GRAVA; por isso é a última)
   Texto igual ao do C#: o ExecuteSqlAsync interpolado vira parâmetro (@p0 = lote, @p1 = '(anônimo)').
   Os lotes de R1 a R5 rodam dentro de uma transação que termina em ROLLBACK, com a contagem de locks.

   Uso:  rodar-a01.sh B (ATRASO=1 para o regime, ATRASO=10 para o acúmulo): roda este arquivo depois do medir-a01.sql.
   ===================================================================== */
:on error exit
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
IF DB_NAME() NOT LIKE N'%Perf%' THROW 50000, N'Banco não descartável: o nome precisa conter "Perf".', 1;
GO

-- Outbox do dataset: o que a API e o Worker deixam lá em regime (entregues dos últimos 7 dias) mais eventos de pedido PARADOS
-- (ProcessadoEmUtc nulo) de 31 a 60 dias, que só a regra nova (E3-D8 item 3) apaga, e eventos de outros tipos.
INSERT infra.EventoOutbox (Tipo, Payload, CriadoEmUtc, ProcessadoEmUtc, Tentativas)
SELECT CHOOSE(1 + g.value % 5, 'PedidoCriado', 'PedidoModerado', 'PedidoTocado', 'ProgramaStatusAlterado', 'TransmissaoAoVivoAlterada'),
       N'{"grupos":["locutores"],"dados":{"pedidoId":"00000000-0000-0000-0000-000000000000","nomeOuvinte":"Ouvinte 1","mensagem":"Um abraço para a galera!"}}',
       DATEADD(SECOND, -(g.value * 20), SYSUTCDATETIME()), DATEADD(SECOND, -(g.value * 20) + 1, SYSUTCDATETIME()), 1
FROM GENERATE_SERIES(1, 30000) AS g;
INSERT infra.EventoOutbox (Tipo, Payload, CriadoEmUtc, ProcessadoEmUtc, Tentativas)
SELECT CHOOSE(1 + g.value % 3, 'PedidoCriado', 'PedidoModerado', 'PedidoTocado'),
       N'{"grupos":["locutores"],"dados":{"pedidoId":"00000000-0000-0000-0000-000000000000"}}',
       DATEADD(DAY, -(31 + g.value % 30), SYSUTCDATETIME()), NULL, 10
FROM GENERATE_SERIES(1, 500) AS g;
GO

PRINT '### 0 o que a retenção encontra para fazer';
SELECT ip_a_anular_7d = SUM(CASE WHEN ListenerIpHash IS NOT NULL AND CriadoEmUtc < DATEADD(DAY, -7, SYSUTCDATETIME()) THEN 1 ELSE 0 END),
       a_anonimizar_30d = SUM(CASE WHEN CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME())
                                    AND (ListenerDeviceHash IS NOT NULL OR ListenerIpHash IS NOT NULL) THEN 1 ELSE 0 END),
       linhas = COUNT(*)
FROM interacao.PedidoMusica;
SELECT eventos = COUNT(*), de_pedido_com_mais_de_30d = SUM(CASE WHEN Tipo IN (N'PedidoCriado', N'PedidoModerado', N'PedidoTocado') AND CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME()) THEN 1 ELSE 0 END),
       entregues_com_mais_de_7d = SUM(CASE WHEN ProcessadoEmUtc < DATEADD(DAY, -7, SYSUTCDATETIME()) THEN 1 ELSE 0 END)
FROM infra.EventoOutbox;
GO

/* =============== R1 a R4: um lote de cada, com ROLLBACK e locks =============== */
DECLARE @sqlIp nvarchar(max) = N'UPDATE TOP (@p0) interacao.PedidoMusica SET ListenerIpHash = NULL
            WHERE ListenerIpHash IS NOT NULL AND CriadoEmUtc < DATEADD(DAY, -7, SYSUTCDATETIME());';
DECLARE @sqlAnon nvarchar(max) = N'UPDATE TOP (@p0) interacao.PedidoMusica SET
                ListenerDeviceHash = NULL,
                ListenerIpHash = NULL,
                UsuarioId = CASE WHEN Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())) THEN NULL ELSE UsuarioId END,
                Mensagem = CASE WHEN Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())) THEN NULL ELSE Mensagem END,
                NomeOuvinte = CASE WHEN Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())) THEN @p1 ELSE NomeOuvinte END
            WHERE CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME())
              AND (ListenerDeviceHash IS NOT NULL OR ListenerIpHash IS NOT NULL
                   OR ((Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())))
                       AND (UsuarioId IS NOT NULL OR Mensagem IS NOT NULL OR NomeOuvinte <> @p1)));';
DECLARE @sqlEvPedido nvarchar(max) = N'DELETE TOP (@p0) FROM infra.EventoOutbox
            WHERE Tipo IN (N''PedidoCriado'', N''PedidoModerado'', N''PedidoTocado'') AND CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME());';
DECLARE @sqlEvVelho nvarchar(max) = N'DELETE TOP (@p0) FROM infra.EventoOutbox WHERE ProcessadoEmUtc < DATEADD(DAY, -7, SYSUTCDATETIME());';
DECLARE @locks nvarchar(max) = N'SELECT i.name AS indice, l.resource_type, l.request_mode, COUNT(*) AS locks
FROM sys.dm_tran_locks AS l
LEFT JOIN sys.partitions AS pa ON pa.hobt_id = l.resource_associated_entity_id AND l.resource_type IN (''KEY'', ''PAGE'', ''RID'', ''HOBT'')
LEFT JOIN sys.indexes AS i ON i.object_id = pa.object_id AND i.index_id = pa.index_id
WHERE l.request_session_id = @@SPID AND l.resource_database_id = DB_ID() AND l.resource_type <> ''DATABASE''
GROUP BY i.name, l.resource_type, l.request_mode ORDER BY locks DESC';
DECLARE @nome nvarchar(60) = N'(anônimo)';

SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### R1 hash do IP aos 7 dias: um lote de 4.000 (ROLLBACK)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
BEGIN TRAN;
EXEC sp_executesql @sqlIp, N'@p0 int', @p0 = 4000;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
EXEC sp_executesql @locks;
ROLLBACK;

SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### R2 anonimização de 30 dias: um lote de 4.000 (ROLLBACK)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
BEGIN TRAN;
EXEC sp_executesql @sqlAnon, N'@p0 int, @p1 nvarchar(60)', @p0 = 4000, @p1 = @nome;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
EXEC sp_executesql @locks;
ROLLBACK;

SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### R3 eventos de pedido do outbox com mais de 30 dias: um lote (ROLLBACK)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
BEGIN TRAN;
EXEC sp_executesql @sqlEvPedido, N'@p0 int', @p0 = 4000;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
ROLLBACK;

SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### R4 limpeza antiga do outbox (entregues há mais de 7 dias): um lote (ROLLBACK)';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
BEGIN TRAN;
EXEC sp_executesql @sqlEvVelho, N'@p0 int', @p0 = 4000;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
ROLLBACK;
GO

/* =============== R5: o índice candidato do A02 =============== */
-- Não é do 03: criado aqui só para medir e removido no fim.
CREATE INDEX IX_PedidoMusica_Anonimizar ON interacao.PedidoMusica (CriadoEmUtc) WHERE ListenerDeviceHash IS NOT NULL;
GO
DECLARE @sqlAnon nvarchar(max) = N'UPDATE TOP (@p0) interacao.PedidoMusica SET
                ListenerDeviceHash = NULL,
                ListenerIpHash = NULL,
                UsuarioId = CASE WHEN Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())) THEN NULL ELSE UsuarioId END,
                Mensagem = CASE WHEN Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())) THEN NULL ELSE Mensagem END,
                NomeOuvinte = CASE WHEN Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())) THEN @p1 ELSE NomeOuvinte END
            WHERE CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME())
              AND (ListenerDeviceHash IS NOT NULL OR ListenerIpHash IS NOT NULL
                   OR ((Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())))
                       AND (UsuarioId IS NOT NULL OR Mensagem IS NOT NULL OR NomeOuvinte <> @p1)));';
-- Variante só com o braço indexável (hash do dispositivo ainda preenchido); o braço raro (terminal tardio) iria numa rotina à parte.
DECLARE @sqlAnonIdx nvarchar(max) = N'UPDATE TOP (@p0) interacao.PedidoMusica SET
                ListenerDeviceHash = NULL,
                ListenerIpHash = NULL,
                UsuarioId = CASE WHEN Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())) THEN NULL ELSE UsuarioId END,
                Mensagem = CASE WHEN Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())) THEN NULL ELSE Mensagem END,
                NomeOuvinte = CASE WHEN Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())) THEN @p1 ELSE NomeOuvinte END
            WHERE CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME()) AND ListenerDeviceHash IS NOT NULL;';
DECLARE @nome nvarchar(60) = N'(anônimo)';
SET STATISTICS IO ON; SET STATISTICS XML ON;
PRINT '### R5a com o índice candidato: o texto do Forge (braço OR) continua igual?';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
BEGIN TRAN;
EXEC sp_executesql @sqlAnon, N'@p0 int, @p1 nvarchar(60)', @p0 = 4000, @p1 = @nome;
ROLLBACK;
PRINT '### R5b com o índice candidato: só o braço do hash do dispositivo';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
BEGIN TRAN;
EXEC sp_executesql @sqlAnonIdx, N'@p0 int, @p1 nvarchar(60)', @p0 = 4000, @p1 = @nome;
ROLLBACK;
SET STATISTICS XML OFF; SET STATISTICS IO OFF;
GO
DROP INDEX IX_PedidoMusica_Anonimizar ON interacao.PedidoMusica;
GO

/* =============== R6: a rotina inteira como o EmLotes do Worker (GRAVA) =============== */
PRINT '### R6 a retenção inteira em lotes de 4.000, uma transação por comando (grava)';
DECLARE @sqlIp nvarchar(max) = N'UPDATE TOP (@p0) interacao.PedidoMusica SET ListenerIpHash = NULL
            WHERE ListenerIpHash IS NOT NULL AND CriadoEmUtc < DATEADD(DAY, -7, SYSUTCDATETIME());';
DECLARE @sqlAnon nvarchar(max) = N'UPDATE TOP (@p0) interacao.PedidoMusica SET
                ListenerDeviceHash = NULL,
                ListenerIpHash = NULL,
                UsuarioId = CASE WHEN Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())) THEN NULL ELSE UsuarioId END,
                Mensagem = CASE WHEN Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())) THEN NULL ELSE Mensagem END,
                NomeOuvinte = CASE WHEN Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())) THEN @p1 ELSE NomeOuvinte END
            WHERE CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME())
              AND (ListenerDeviceHash IS NOT NULL OR ListenerIpHash IS NOT NULL
                   OR ((Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())))
                       AND (UsuarioId IS NOT NULL OR Mensagem IS NOT NULL OR NomeOuvinte <> @p1)));';
DECLARE @sqlEvPedido nvarchar(max) = N'DELETE TOP (@p0) FROM infra.EventoOutbox
            WHERE Tipo IN (N''PedidoCriado'', N''PedidoModerado'', N''PedidoTocado'') AND CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME());';
DECLARE @sqlEvVelho nvarchar(max) = N'DELETE TOP (@p0) FROM infra.EventoOutbox WHERE ProcessadoEmUtc < DATEADD(DAY, -7, SYSUTCDATETIME());';
DECLARE @nome nvarchar(60) = N'(anônimo)';
DECLARE @res TABLE (passo varchar(30), lotes int, linhas int, ms int);
DECLARE @t0 datetime2(3), @n int, @lotes int, @total int, @p int;
DECLARE @promo0 bigint = (SELECT ISNULL(SUM(index_lock_promotion_count), 0) FROM sys.dm_db_index_operational_stats(DB_ID(), OBJECT_ID('interacao.PedidoMusica'), NULL, NULL));
DECLARE @log0 bigint = (SELECT used_log_space_in_bytes FROM sys.dm_db_log_space_usage);

SELECT @t0 = SYSUTCDATETIME(), @lotes = 0, @total = 0, @n = 4000;
WHILE @n = 4000 BEGIN EXEC sp_executesql @sqlIp, N'@p0 int', @p0 = 4000; SET @n = @@ROWCOUNT; SET @lotes += 1; SET @total += @n; END;
INSERT @res VALUES ('ip 7 dias', @lotes, @total, DATEDIFF(MILLISECOND, @t0, SYSUTCDATETIME()));

SELECT @t0 = SYSUTCDATETIME(), @lotes = 0, @total = 0, @n = 4000;
WHILE @n = 4000 BEGIN EXEC sp_executesql @sqlAnon, N'@p0 int, @p1 nvarchar(60)', @p0 = 4000, @p1 = @nome; SET @n = @@ROWCOUNT; SET @lotes += 1; SET @total += @n; END;
INSERT @res VALUES ('anonimiza 30 dias', @lotes, @total, DATEDIFF(MILLISECOND, @t0, SYSUTCDATETIME()));

SELECT @t0 = SYSUTCDATETIME(), @lotes = 0, @total = 0, @n = 4000;
WHILE @n = 4000 BEGIN EXEC sp_executesql @sqlEvPedido, N'@p0 int', @p0 = 4000; SET @n = @@ROWCOUNT; SET @lotes += 1; SET @total += @n; END;
INSERT @res VALUES ('eventos de pedido 30d', @lotes, @total, DATEDIFF(MILLISECOND, @t0, SYSUTCDATETIME()));

SELECT @t0 = SYSUTCDATETIME(), @lotes = 0, @total = 0, @n = 4000;
WHILE @n = 4000 BEGIN EXEC sp_executesql @sqlEvVelho, N'@p0 int', @p0 = 4000; SET @n = @@ROWCOUNT; SET @lotes += 1; SET @total += @n; END;
INSERT @res VALUES ('outbox entregue 7d', @lotes, @total, DATEDIFF(MILLISECOND, @t0, SYSUTCDATETIME()));

SELECT * FROM @res;
SELECT promocoes_de_lock = (SELECT ISNULL(SUM(index_lock_promotion_count), 0) FROM sys.dm_db_index_operational_stats(DB_ID(), OBJECT_ID('interacao.PedidoMusica'), NULL, NULL)) - @promo0,
       log_usado_kb = ((SELECT used_log_space_in_bytes FROM sys.dm_db_log_space_usage) - @log0) / 1024;
-- Segunda passada: idempotente, o que sobra tem de ser zero e o custo é o do "dia em que não há nada a fazer".
SET STATISTICS IO ON;
PRINT '### R6b segunda passada da anonimização (nada a fazer): o custo do dia vazio';
CHECKPOINT; DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS; DBCC FREEPROCCACHE WITH NO_INFOMSGS;
EXEC sp_executesql @sqlAnon, N'@p0 int, @p1 nvarchar(60)', @p0 = 4000, @p1 = @nome;
SET STATISTICS IO OFF;
GO

PRINT '### R7 o que sobra depois da retenção (tem de ser zero em cada coluna)';
SELECT ip_com_mais_de_7d = SUM(CASE WHEN ListenerIpHash IS NOT NULL AND CriadoEmUtc < DATEADD(DAY, -7, SYSUTCDATETIME()) THEN 1 ELSE 0 END),
       hash_dispositivo_com_mais_de_30d = SUM(CASE WHEN ListenerDeviceHash IS NOT NULL AND CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME()) THEN 1 ELSE 0 END),
       terminal_com_mais_de_30d_e_texto = SUM(CASE WHEN CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME())
                                                    AND (Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())))
                                                    AND (UsuarioId IS NOT NULL OR Mensagem IS NOT NULL OR NomeOuvinte <> N'(anônimo)') THEN 1 ELSE 0 END),
       pendente_ou_aprovado_recente_anonimizado = SUM(CASE WHEN CriadoEmUtc >= DATEADD(DAY, -30, SYSUTCDATETIME()) AND NomeOuvinte = N'(anônimo)' THEN 1 ELSE 0 END),
       eventos_de_pedido_com_mais_de_30d = (SELECT COUNT(*) FROM infra.EventoOutbox WHERE Tipo IN (N'PedidoCriado', N'PedidoModerado', N'PedidoTocado') AND CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME()))
FROM interacao.PedidoMusica;
GO
