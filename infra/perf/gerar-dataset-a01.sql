/* =====================================================================
   WebRadio — infra/perf/gerar-dataset-a01.sql
   Autor: Atlas (DBA) · tarefa E3-A01 · 2026-10-29

   Dataset sintético para medir os planos dos pedidos de música e das divulgações (07, E3-A01 e E3-A02).
   NÃO é seed de desenvolvimento: só roda em banco DESCARTÁVEL, depois do gerar-dataset-a02.sql (reaproveita os
   locutores e os programas dele). Não entra em nenhuma imagem nem no compose.

   Trava (fail-closed): aborta se o banco não tiver "Perf" no nome, se já houver pedidos ou divulgações, ou se o
   dataset do A02 não tiver sido carregado.

   Uso (sqlcmd): -v CENARIO=A   realista: 40 Pendente recentes, 40 divulgações
                 -v CENARIO=B   acúmulo (Worker parado): 25.000 Pendente vencidos, 40 divulgações + 10.000 inativas
                 -v ATRASO=1    dias de atraso da retenção do E3-D8 (1 = regime; 10 = job parado há 10 dias)

   Forma do dataset (tudo relativo ao SYSUTCDATETIME() da carga; meça logo depois de carregar):
     - 500.000 pedidos no total, espalhados por 365 dias. Terminais: Rejeitado 25%, Tocado 20%, Aprovado 15% (pior
       caso: Aprovado que o locutor nunca marcou como tocado), Expirado 40%.
     - 300 pedidos moderados nas últimas 24 h (40 Aprovado, 100 Rejeitado, 160 Tocado): alimentam a lista do E3-D9 (a distribuição de 365 dias já põe ~200 Aprovado nessa faixa; o total medido sai no resumo).
     - Pendente: A = 40 nos últimos 100 min; B = 25.000 de 2 h a 48 h atrás (vencidos, o Worker não expirou).
     - Retenção do E3-D8 com ATRASO dias de atraso em cada corte (o que o A02 mede por execução):
         ListenerIpHash       preenchido só se CriadoEmUtc >= agora - (7 + ATRASO) dias
         ListenerDeviceHash   preenchido só se CriadoEmUtc >= agora - (30 + ATRASO) dias
         Antes de (30 + ATRASO) dias: UsuarioId NULL, Mensagem NULL, NomeOuvinte = '(anônimo)' (todos terminais).
     - Dispositivos: 20.000, com cauda longa (POWER(rnd, 2)): o mais ativo concentra ~0,7% dos pedidos.
       IPs: 6.000, cerca de 3 dispositivos por IP; nos últimos 7 dias 10% dos pedidos saem de UM só IP ('ip-hot', o caso
       do KnownIPNetworks errado, em que todo mundo parece vir do nginx).
     - 8% dos pedidos de um dos 2.000 ouvintes cadastrados (Role 3); 70% ligados a um programa Finalizado.
     - Divulgações: 8 ativas sem janela, 4 com janela em curso, 3 com janela vencida, 3 com janela futura, o resto
       inativo; 70% com imagem (chave no formato do E3-D5).
   ===================================================================== */

:on error exit
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

IF DB_NAME() NOT LIKE N'%Perf%'
    THROW 50000, N'Banco não descartável: o nome precisa conter "Perf".', 1;
IF EXISTS (SELECT 1 FROM interacao.PedidoMusica) OR EXISTS (SELECT 1 FROM interacao.Divulgacao)
    THROW 50000, N'Já há pedidos ou divulgações: gere o dataset só num banco recém-criado.', 1;
IF (SELECT COUNT(*) FROM seg.Usuario WHERE Role = 2) < 50 OR NOT EXISTS (SELECT 1 FROM grade.Programa WHERE Status = 3)
    THROW 50000, N'Carregue o gerar-dataset-a02.sql antes (locutores e programas).', 1;
IF N'$(ATRASO)' NOT LIKE N'[0-9]%' OR LEN(N'$(ATRASO)') > 3
    THROW 50000, N'Passe -v ATRASO=<dias> (1 = regime).', 1;
IF N'$(CENARIO)' NOT IN (N'A', N'B')
    THROW 50000, N'Passe -v CENARIO=A ou -v CENARIO=B.', 1;
GO

DECLARE @Cenario char(1) = '$(CENARIO)';
DECLARE @Total int = 500000, @Recentes int = 300;
DECLARE @Pendentes int = CASE @Cenario WHEN 'A' THEN 40 ELSE 25000 END;
DECLARE @agora datetime2(3) = SYSUTCDATETIME();
-- ATRASO = dias de atraso da retenção (1 = regime, o job do dia anterior rodou; 10 = o job ficou parado e há acúmulo).
DECLARE @atraso int = $(ATRASO);
DECLARE @corteIp datetime2(3) = DATEADD(DAY, -(7 + @atraso), @agora), @corteDispositivo datetime2(3) = DATEADD(DAY, -(30 + @atraso), @agora);

/* ---------- ouvintes cadastrados, locutores e programas (listas numeradas para o sorteio) ---------- */
INSERT seg.Usuario (Nome, Email, EmailNormalizado, SenhaHash, Role)
SELECT CONCAT(N'Ouvinte ', x.n), CONCAT(N'ouvinte', x.n, N'@perf.invalid'),
       UPPER(CONCAT(N'ouvinte', x.n, N'@perf.invalid')), N'sem-senha', 3
FROM GENERATE_SERIES(1, 2000) AS g
CROSS APPLY (SELECT CONVERT(nvarchar(10), g.value) AS n) AS x;

SELECT rn = ROW_NUMBER() OVER (ORDER BY Id), Id INTO #ouvinte FROM seg.Usuario WHERE Role = 3;
SELECT rn = ROW_NUMBER() OVER (ORDER BY Id), Id INTO #locutor FROM seg.Usuario WHERE Role = 2;
SELECT TOP (20000) rn = ROW_NUMBER() OVER (ORDER BY Id), Id INTO #programa FROM grade.Programa WHERE Status = 3;
DECLARE @nOuvinte int = (SELECT COUNT(*) FROM #ouvinte), @nLocutor int = (SELECT COUNT(*) FROM #locutor),
        @nPrograma int = (SELECT COUNT(*) FROM #programa);

/* ---------- pedidos: primeiro a base (quando e em que estado), depois as colunas ---------- */
CREATE TABLE #base (n int IDENTITY(1,1) PRIMARY KEY, CriadoEmUtc datetime2(3) NOT NULL, Status tinyint NOT NULL);

-- Terminais espalhados por 365 dias, de 3 h atrás para trás.
INSERT #base (CriadoEmUtc, Status)
SELECT DATEADD(MILLISECOND, ABS(CHECKSUM(NEWID())) % 1000,
               DATEADD(SECOND, -(ABS(CHECKSUM(NEWID())) % 31536000), DATEADD(HOUR, -3, @agora))),
       CASE WHEN r.v < 25 THEN 3 WHEN r.v < 45 THEN 4 WHEN r.v < 60 THEN 2 ELSE 5 END
FROM GENERATE_SERIES(1, @Total - @Pendentes - @Recentes) AS g
CROSS APPLY (SELECT ABS(CHECKSUM(NEWID())) % 100 AS v) AS r;

-- Moderados nas últimas 24 h: 40 Aprovado, 100 Rejeitado, 160 Tocado (criados entre 24 h e 3 h atrás).
INSERT #base (CriadoEmUtc, Status)
SELECT DATEADD(SECOND, -(3 * 3600 + ABS(CHECKSUM(NEWID())) % (21 * 3600)), @agora),
       CASE WHEN g.value <= 40 THEN 2 WHEN g.value <= 140 THEN 3 ELSE 4 END
FROM GENERATE_SERIES(1, @Recentes) AS g;

-- Pendentes: A = recentes (nos últimos 100 min); B = vencidos (de 2 h a 48 h atrás).
INSERT #base (CriadoEmUtc, Status)
SELECT CASE @Cenario
         WHEN 'A' THEN DATEADD(SECOND, -(ABS(CHECKSUM(NEWID())) % (100 * 60)), @agora)
         ELSE DATEADD(SECOND, -(2 * 3600 + ABS(CHECKSUM(NEWID())) % (46 * 3600)), @agora)
       END, 1
FROM GENERATE_SERIES(1, @Pendentes) AS g;

-- Colunas derivadas. rnd em [0,1) para a cauda longa dos dispositivos.
SELECT b.n, b.CriadoEmUtc, b.Status,
       dev = 1 + CONVERT(int, 20000 * POWER((ABS(CHECKSUM(NEWID())) % 1000000) / 1000000.0, 2)),
       quente = CASE WHEN b.CriadoEmUtc >= DATEADD(DAY, -7, @agora) AND ABS(CHECKSUM(NEWID())) % 10 = 0 THEN 1 ELSE 0 END,
       mod_min = 1 + ABS(CHECKSUM(NEWID())) % 90,
       toc_min = 5 + ABS(CHECKSUM(NEWID())) % 56,
       com_usuario = CASE WHEN ABS(CHECKSUM(NEWID())) % 100 < 8 THEN 1 ELSE 0 END,
       com_programa = CASE WHEN ABS(CHECKSUM(NEWID())) % 100 < 70 THEN 1 ELSE 0 END,
       com_mensagem = CASE WHEN ABS(CHECKSUM(NEWID())) % 100 < 60 THEN 1 ELSE 0 END,
       sorteio = ABS(CHECKSUM(NEWID()))
INTO #deriv
FROM #base AS b;

INSERT interacao.PedidoMusica
    (ProgramaId, UsuarioId, NomeOuvinte, TituloMusica, Artista, Mensagem, ListenerDeviceHash, ListenerIpHash, Status,
     ModeradoPorUsuarioId, ModeradoEmUtc, MotivoRejeicao, TocadoEmUtc, CriadoEmUtc)
SELECT
    CASE WHEN d.com_programa = 1 THEN p.Id END,
    CASE WHEN d.com_usuario = 1 AND d.CriadoEmUtc >= @corteDispositivo THEN o.Id END,
    CASE WHEN d.CriadoEmUtc >= @corteDispositivo THEN CONCAT(N'Ouvinte ', d.dev) ELSE N'(anônimo)' END,
    CONCAT(N'Faixa ', d.sorteio % 5000, N' - versão ', d.sorteio % 7),
    CONCAT(N'Artista ', d.sorteio % 800),
    CASE WHEN d.com_mensagem = 1 AND d.CriadoEmUtc >= @corteDispositivo
         THEN REPLICATE(N'Um abraço para a galera! ', 1 + d.sorteio % 4) END,
    CASE WHEN d.CriadoEmUtc >= @corteDispositivo THEN HASHBYTES('SHA2_256', CONCAT(N'dev-', d.dev)) END,
    CASE WHEN d.CriadoEmUtc >= @corteIp
         THEN HASHBYTES('SHA2_256', CASE WHEN d.quente = 1 THEN N'ip-hot' ELSE CONCAT(N'ip-', d.dev % 6000) END) END,
    d.Status,
    CASE WHEN d.Status IN (2, 3, 4) THEN l.Id END,
    CASE WHEN d.Status IN (2, 3, 4) THEN DATEADD(MINUTE, d.mod_min, d.CriadoEmUtc) END,
    CASE WHEN d.Status = 3 THEN N'Fora da programação' END,
    CASE WHEN d.Status = 4 THEN DATEADD(MINUTE, d.mod_min + d.toc_min, d.CriadoEmUtc) END,
    d.CriadoEmUtc
FROM #deriv AS d
LEFT JOIN #programa AS p ON p.rn = d.n % @nPrograma + 1
LEFT JOIN #ouvinte AS o ON o.rn = d.n % @nOuvinte + 1
LEFT JOIN #locutor AS l ON l.rn = d.n % @nLocutor + 1
ORDER BY d.CriadoEmUtc;   -- Id sequencial na ordem do tempo, como o NEWSEQUENTIALID de produção

DROP TABLE #deriv, #base, #ouvinte, #locutor, #programa;

/* ---------- divulgações ---------- */
DECLARE @admin uniqueidentifier = (SELECT TOP (1) Id FROM seg.Usuario WHERE Role = 1 ORDER BY Id);
DECLARE @inativas int = CASE @Cenario WHEN 'A' THEN 25 ELSE 10025 END;
DECLARE @agora0 datetime2(0) = CONVERT(datetime2(0), @agora);

INSERT interacao.Divulgacao (Titulo, Mensagem, ImagemChaveStorage, LinkDestino, Ativo, Prioridade, InicioExibicaoUtc,
                             FimExibicaoUtc, CriadoPorUsuarioId, CriadoEmUtc)
SELECT CONCAT(N'Divulgação ', g.value), REPLICATE(N'Texto da divulgação com chamada para a programação. ', 1 + g.value % 6),
       CASE WHEN g.value % 10 < 7 THEN CONCAT('divulgacoes/', LOWER(REPLACE(CONVERT(varchar(36), NEWID()), '-', '')), '.webp') END,
       CASE WHEN g.value % 2 = 0 THEN CONCAT(N'https://exemplo.perf.invalid/', g.value) END,
       CASE WHEN g.value <= 18 THEN 1 ELSE 0 END,
       g.value % 101,
       CASE WHEN g.value BETWEEN 9 AND 12 THEN DATEADD(DAY, -3, @agora0)     -- janela em curso
            WHEN g.value BETWEEN 13 AND 15 THEN DATEADD(DAY, -30, @agora0)   -- janela vencida
            WHEN g.value BETWEEN 16 AND 18 THEN DATEADD(DAY, 5, @agora0) END, -- janela futura
       CASE WHEN g.value BETWEEN 9 AND 12 THEN DATEADD(DAY, 3, @agora0)
            WHEN g.value BETWEEN 13 AND 15 THEN DATEADD(DAY, -10, @agora0)
            WHEN g.value BETWEEN 16 AND 18 THEN DATEADD(DAY, 20, @agora0) END,
       @admin, DATEADD(DAY, -(g.value % 400), @agora)
FROM GENERATE_SERIES(1, 18 + @inativas) AS g;
GO

/* ---------- estatísticas como no A02 (padrão, por amostra) e resumo ---------- */
UPDATE STATISTICS interacao.PedidoMusica;
UPDATE STATISTICS interacao.Divulgacao;
UPDATE STATISTICS seg.Usuario;
GO

PRINT '### pedidos por status (1 Pendente, 2 Aprovado, 3 Rejeitado, 4 Tocado, 5 Expirado)';
SELECT Status, COUNT(*) AS linhas,
       SUM(CASE WHEN ListenerDeviceHash IS NOT NULL THEN 1 ELSE 0 END) AS com_hash_dispositivo,
       SUM(CASE WHEN ListenerIpHash IS NOT NULL THEN 1 ELSE 0 END) AS com_hash_ip,
       SUM(CASE WHEN UsuarioId IS NOT NULL THEN 1 ELSE 0 END) AS com_usuario,
       SUM(CASE WHEN Mensagem IS NOT NULL THEN 1 ELSE 0 END) AS com_mensagem
FROM interacao.PedidoMusica GROUP BY Status ORDER BY Status;

PRINT '### o que o A02 anonimiza por execução (um dia de atraso em cada corte)';
SELECT ip_a_anular_7d = SUM(CASE WHEN ListenerIpHash IS NOT NULL AND CriadoEmUtc < DATEADD(DAY, -7, SYSUTCDATETIME()) THEN 1 ELSE 0 END),
       dispositivo_a_anular_30d = SUM(CASE WHEN ListenerDeviceHash IS NOT NULL AND CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME()) THEN 1 ELSE 0 END),
       com_hash_dispositivo = SUM(CASE WHEN ListenerDeviceHash IS NOT NULL THEN 1 ELSE 0 END),
       aprovados_24h = SUM(CASE WHEN Status = 2 AND ModeradoEmUtc >= DATEADD(HOUR, -24, SYSUTCDATETIME()) THEN 1 ELSE 0 END),
       pendentes_vencidos = SUM(CASE WHEN Status = 1 AND CriadoEmUtc < DATEADD(MINUTE, -120, SYSUTCDATETIME()) THEN 1 ELSE 0 END),
       dispositivos_distintos = COUNT(DISTINCT ListenerDeviceHash),
       ips_distintos = COUNT(DISTINCT ListenerIpHash),
       pedidos_do_ip_quente = SUM(CASE WHEN ListenerIpHash = HASHBYTES('SHA2_256', N'ip-hot') THEN 1 ELSE 0 END)
FROM interacao.PedidoMusica;

PRINT '### divulgações';
SELECT total = COUNT(*), ativas = SUM(CONVERT(int, Ativo)), com_imagem = SUM(CASE WHEN ImagemChaveStorage IS NOT NULL THEN 1 ELSE 0 END)
FROM interacao.Divulgacao;

PRINT '### tamanho por índice de PedidoMusica (MB)';
SELECT i.name AS indice, CONVERT(decimal(10,2), SUM(ps.used_page_count) * 8 / 1024.0) AS mb, SUM(ps.row_count) AS linhas
FROM sys.dm_db_partition_stats AS ps
JOIN sys.indexes AS i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
WHERE ps.object_id = OBJECT_ID(N'interacao.PedidoMusica')
GROUP BY i.name ORDER BY mb DESC;
GO
