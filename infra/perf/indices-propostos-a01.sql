/* =====================================================================
   WebRadio — infra/perf/indices-propostos-a01.sql
   Autor: Atlas (DBA) · tarefa E3-A01 · 2026-10-29

   Índices que o 07 (v1.4, "Contratos alterados / DDL") acrescenta ao 03 e que o PR C1/C2 ainda vai
   migrar. Só para o banco de medição: o rodar-a01.sh aplica este arquivo logo depois do 03. Cada índice
   só é criado se o 03 ainda não o tiver, então o arquivo continua valendo depois do merge do C1/C2.
   ===================================================================== */
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PedidoMusica_Aprovados' AND object_id = OBJECT_ID(N'interacao.PedidoMusica'))
    CREATE INDEX IX_PedidoMusica_Aprovados ON interacao.PedidoMusica (ModeradoEmUtc)
        INCLUDE (NomeOuvinte, TituloMusica, Artista, Mensagem, ProgramaId, CriadoEmUtc) WHERE Status = 2;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Divulgacao_ImagemChave' AND object_id = OBJECT_ID(N'interacao.Divulgacao'))
    CREATE UNIQUE INDEX UX_Divulgacao_ImagemChave ON interacao.Divulgacao (ImagemChaveStorage) WHERE ImagemChaveStorage IS NOT NULL;
GO
