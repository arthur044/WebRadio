/* =====================================================================
   WebRadio — infra/perf/gerar-dataset-a02.sql
   Autor: Atlas (DBA) · tarefa E2-A02 · 2026-10-20

   Dataset sintético para medir os planos da grade (interseção, Worker e janela do
   harbor/auth). NÃO é seed de desenvolvimento: só roda em banco DESCARTÁVEL, vazio, com
   o schema do 03 já aplicado. Não entra em nenhuma imagem nem no compose.

   Trava (fail-closed): aborta se o banco não tiver "Perf" no nome ou se já houver linhas
   em grade.Programa / seg.Usuario. Assim ele nunca toca o banco WebRadio do stack.

   Parâmetros (variáveis @Anos, @PorDia, @CancPorDia e @DiasFuturos, no início do segundo lote):
     Anos 5 · ProgramasPorDia 110 · CanceladosPorDia 10 · DiasFuturos 30

   Forma do dataset (por dia, hoje ± o horizonte):
     - Encadeados: ProgramasPorDia programas em fila, 5 a 13 min cada, sem sobreposição
       (intervalos semiabertos que se encostam). Passado = Finalizado; o que cobre "agora" =
       AoVivo (um só); futuro = Agendado. É a única classe que respeita a regra de
       não-sobreposição, porque só Agendado/AoVivo precisam respeitá-la (00, Fluxo A).
     - Longos: 1 a cada 5 dias, 6 a 12 h (o teto do CK_Programa_DuracaoMax), Finalizado, no
       passado. Cobrem o pior caso do limite de 12 h na faixa do índice.
     - Cancelados: CanceladosPorDia por dia, 5 min a 2 h (5% chegam a 12 h), no passado.
       Podem se sobrepor entre si e aos demais: Cancelado não conta na grade.
   Com os padrões: ~219 mil programas no passado + ~3,3 mil Agendado + 1 AoVivo.
   50 locutores, 2 admins, 1 credencial ativa e 2 revogadas por locutor.
   ===================================================================== */

:on error exit
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

IF DB_NAME() NOT LIKE N'%Perf%'
    THROW 50000, N'Banco não descartável: o nome precisa conter "Perf".', 1;
IF EXISTS (SELECT 1 FROM grade.Programa) OR EXISTS (SELECT 1 FROM seg.Usuario)
    THROW 50000, N'Banco não está vazio: gere o dataset só num banco recém-criado.', 1;
GO

DECLARE @Anos int = 5, @PorDia int = 110, @CancPorDia int = 10, @DiasFuturos int = 30;   -- ajuste aqui, se precisar
IF @PorDia NOT BETWEEN 1 AND 160
    THROW 50000, N'ProgramasPorDia precisa estar entre 1 e 160 (5 min por programa na fila de 24 h).', 1;

DECLARE @agora datetime2(0) = CONVERT(datetime2(0), SYSUTCDATETIME());
DECLARE @hoje datetime2(0) = CONVERT(datetime2(0), CONVERT(date, @agora));
DECLARE @primeiroDia datetime2(0) = DATEADD(YEAR, -@Anos, @hoje);
DECLARE @slot int = 86400 / @PorDia;                     -- segundos por posição da fila

/* ---------- usuários e credenciais ---------- */
INSERT seg.Usuario (Nome, Email, EmailNormalizado, SenhaHash, Role)
SELECT CONCAT(N'Locutor ', x.n), CONCAT(N'locutor', x.n, N'@perf.invalid'),
       UPPER(CONCAT(N'locutor', x.n, N'@perf.invalid')), N'sem-senha', 2
FROM GENERATE_SERIES(1, 50) AS g
CROSS APPLY (SELECT FORMAT(CONVERT(int, g.value), '000') AS n) AS x;

INSERT seg.Usuario (Nome, Email, EmailNormalizado, SenhaHash, Role)
VALUES (N'Admin 1', N'admin1@perf.invalid', N'ADMIN1@PERF.INVALID', N'sem-senha', 1),
       (N'Admin 2', N'admin2@perf.invalid', N'ADMIN2@PERF.INVALID', N'sem-senha', 1);

-- Duas credenciais revogadas (histórico de rotação) e uma ativa por locutor.
INSERT seg.CredencialTransmissao (UsuarioId, Identificador, SegredoHash, Salt, CriadaEmUtc, RevogadaEmUtc)
SELECT u.Id, NEWID(), CRYPT_GEN_RANDOM(32), CRYPT_GEN_RANDOM(16),
       DATEADD(DAY, -90 * r.n - 10, @agora), DATEADD(DAY, -90 * r.n, @agora)
FROM seg.Usuario u CROSS JOIN (VALUES (1), (2)) AS r(n) WHERE u.Role = 2;
INSERT seg.CredencialTransmissao (UsuarioId, Identificador, SegredoHash, Salt, CriadaEmUtc)
SELECT u.Id, NEWID(), CRYPT_GEN_RANDOM(32), CRYPT_GEN_RANDOM(16), DATEADD(DAY, -5, @agora)
FROM seg.Usuario u WHERE u.Role = 2;

/* ---------- programas (preparo em tabela temporária; o INSERT final é em ordem cronológica) ---------- */
CREATE TABLE #Loc (Idx int NOT NULL PRIMARY KEY, Id uniqueidentifier NOT NULL);
INSERT #Loc SELECT ROW_NUMBER() OVER (ORDER BY Nome) - 1, Id FROM seg.Usuario WHERE Role = 2;

CREATE TABLE #P (
    InicioUtc datetime2(0) NOT NULL, FimUtc datetime2(0) NOT NULL, Classe char(1) NOT NULL, Aleat1 int NOT NULL, Aleat2 int NOT NULL);

DECLARE @dias int = DATEDIFF(DAY, @primeiroDia, @hoje) + @DiasFuturos + 1;

-- Encadeados: posição k do dia começa em k*@slot; duração 300..(@slot-1) s, sempre cabendo na posição.
INSERT #P (InicioUtc, FimUtc, Classe, Aleat1, Aleat2)
SELECT i.InicioUtc, DATEADD(SECOND, 300 + ABS(CHECKSUM(NEWID(), d.value, k.value, 1)) % (@slot - 300), i.InicioUtc), 'E',
       ABS(CHECKSUM(NEWID(), d.value, k.value, 2)), ABS(CHECKSUM(NEWID(), d.value, k.value, 3))
FROM GENERATE_SERIES(0, @dias - 1) AS d
CROSS JOIN GENERATE_SERIES(0, @PorDia - 1) AS k
CROSS APPLY (SELECT DATEADD(SECOND, CONVERT(int, k.value) * @slot, DATEADD(DAY, CONVERT(int, d.value), @primeiroDia)) AS InicioUtc) AS i;

-- Longos: 1 a cada 5 dias, 6..12 h, só até anteontem (fim sempre no passado).
INSERT #P (InicioUtc, FimUtc, Classe, Aleat1, Aleat2)
SELECT i.InicioUtc, DATEADD(SECOND, 21600 + a.R2 % 21601, i.InicioUtc), 'L', a.R, a.R2
FROM GENERATE_SERIES(0, DATEDIFF(DAY, @primeiroDia, @hoje) - 2, 5) AS d
CROSS APPLY (SELECT ABS(CHECKSUM(NEWID(), d.value, 1)) AS R, ABS(CHECKSUM(NEWID(), d.value, 2)) AS R2) AS a
CROSS APPLY (SELECT DATEADD(SECOND, a.R % 86400, DATEADD(DAY, CONVERT(int, d.value), @primeiroDia)) AS InicioUtc) AS i;

-- Cancelados: só no passado (até ontem). 95% de 5 min a 2 h, 5% de até 12 h.
INSERT #P (InicioUtc, FimUtc, Classe, Aleat1, Aleat2)
SELECT i.InicioUtc, DATEADD(SECOND, 300 + CASE WHEN a.R % 20 = 0 THEN a.R2 % 42901 ELSE a.R2 % 6901 END, i.InicioUtc), 'C', a.R, a.R2
FROM GENERATE_SERIES(0, DATEDIFF(DAY, @primeiroDia, @hoje) - 1) AS d
CROSS JOIN GENERATE_SERIES(1, @CancPorDia) AS k
CROSS APPLY (SELECT ABS(CHECKSUM(NEWID(), d.value, k.value, 1)) AS R, ABS(CHECKSUM(NEWID(), d.value, k.value, 2)) AS R2) AS a
CROSS APPLY (SELECT DATEADD(SECOND, a.R2 % 86400, DATEADD(DAY, CONVERT(int, d.value), @primeiroDia)) AS InicioUtc) AS i;

-- Em ordem cronológica: o NEWSEQUENTIALID acompanha o InicioUtc, como num banco que cresceu aos poucos.
INSERT grade.Programa (Titulo, Descricao, LocutorId, InicioUtc, FimUtc, Status, CanceladoEmUtc)
SELECT CONCAT(N'Programa ', p.Aleat1 % 100000, N' — ', REPLICATE(N'x', 10 + p.Aleat2 % 50)),
       CASE WHEN p.Aleat2 % 2 = 0 THEN REPLICATE(N'descrição ', 5 + p.Aleat1 % 15) END,
       l.Id, p.InicioUtc, p.FimUtc,
       CASE WHEN p.Classe = 'C' THEN 4
            WHEN p.FimUtc <= @agora THEN 3
            WHEN p.InicioUtc <= @agora THEN 2
            ELSE 1 END,
       CASE WHEN p.Classe = 'C' THEN DATEADD(DAY, -1, p.InicioUtc) END
FROM #P p JOIN #Loc l ON l.Idx = p.Aleat1 % 50
ORDER BY p.InicioUtc, p.Classe;

DROP TABLE #P; DROP TABLE #Loc;

-- Garante um AoVivo agora: se "agora" caiu numa folga entre dois encadeados, o anterior vai até o início do próximo
-- (intervalos encostados, duração < 13 min, sem sobrepor).
IF NOT EXISTS (SELECT 1 FROM grade.Programa WHERE Status = 2)
BEGIN
    DECLARE @proximo datetime2(0) = (SELECT MIN(InicioUtc) FROM grade.Programa WHERE Status = 1);
    UPDATE grade.Programa SET FimUtc = @proximo, Status = 2
    WHERE Id = (SELECT TOP (1) Id FROM grade.Programa
                WHERE Status = 3 AND FimUtc <= @agora AND DATEDIFF(MINUTE, InicioUtc, FimUtc) < 13 ORDER BY InicioUtc DESC);
END;

-- Estatística como o auto-update deixaria (amostra padrão), e não FULLSCAN.
UPDATE STATISTICS grade.Programa;
UPDATE STATISTICS seg.Usuario;
UPDATE STATISTICS seg.CredencialTransmissao;

/* ---------- conferência ---------- */
SELECT Status, COUNT(*) AS Programas, MIN(InicioUtc) AS Primeiro, MAX(InicioUtc) AS Ultimo,
       MAX(DATEDIFF(MINUTE, InicioUtc, FimUtc)) AS MaxMinutos
FROM grade.Programa GROUP BY Status ORDER BY Status;

-- Invariante da grade: nenhum par Agendado/AoVivo se sobrepõe (tem que dar 0).
SELECT COUNT(*) AS ParesAtivosSobrepostos
FROM grade.Programa a JOIN grade.Programa b
  ON a.Id < b.Id AND a.Status IN (1, 2) AND b.Status IN (1, 2) AND a.InicioUtc < b.FimUtc AND b.InicioUtc < a.FimUtc;
GO
