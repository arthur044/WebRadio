/* =====================================================================
   WebRadio — Schema de referência v1 (SQL Server 2022)
   Autor: Nexus (Planejador) · 2026-09-27 · Dono da revisão: Atlas (DBA)

   Revisão E1-A01 (Atlas, 2026-09-27): aprovado com ajustes — ver comentários
   "-- [Atlas]" (achados da revisão de tipos/CHECKs/índices/collation) e
   "-- [Atlas/v1.1]" (S-A12/S-B07/S-M02/S-M03/S-M04, auditoria do Sentinel em
   docs/specs/05-seguranca-auditoria.md, incorporados a pedido do Nexus).
   Não compilado contra uma instância real nesta revisão: Docker/WSL2
   indisponível no ambiente do Atlas (sem virtualização aninhada). Forge deve
   validar via Testcontainers (E1-F04) e reportar qualquer erro de compilação.

   Papel deste arquivo (ADR D14):
     - Contrato do modelo relacional. As EF Core Migrations do Forge são
       a fonte EXECUTÁVEL e precisam gerar um schema equivalente
       (mesmos nomes, tipos, constraints e índices).
     - O Atlas compara com `dotnet ef migrations script --idempotent`.

   Convenções:
     - PK uniqueidentifier com NEWSEQUENTIALID() (o EF gera GUID sequencial
       do lado do cliente, compatível com a ordenação do SQL Server).
     - Datas em UTC: datetime2, sufixo "Utc". Nunca datetime/datetimeoffset.
     - Enums em tinyint com CHECK (valores em docs/specs/01-dominio.md §1).
     - Nomes de constraint: PK_, FK_, UX_, IX_, CK_, DF_ + Tabela_Coluna.
     - Concorrência otimista: coluna rowversion "RowVersion".
   ===================================================================== */

/* ---------- 0. Banco ---------- */
-- Criado pelo serviço "migrator" (Épico 1). Collation acento-insensível para
-- busca de título/artista ("Canção" = "cancao").
-- CREATE DATABASE WebRadio COLLATE Latin1_General_100_CI_AI_SC;
-- GO
-- ALTER DATABASE WebRadio SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
-- ALTER DATABASE WebRadio SET ALLOW_SNAPSHOT_ISOLATION ON;
-- ALTER DATABASE WebRadio SET COMPATIBILITY_LEVEL = 160;
-- GO
/* Com RCSI ligado, leitores não bloqueiam escritores. Por isso a regra de
   não sobreposição da grade usa sp_getapplock (ADR D7), não trigger. */

/* ---------- 1. Schemas ---------- */
CREATE SCHEMA seg;        -- identidade e acesso
GO
CREATE SCHEMA grade;      -- programação
GO
CREATE SCHEMA interacao;  -- pedidos e divulgações
GO
CREATE SCHEMA midia;      -- arquivos e histórico de reprodução
GO
CREATE SCHEMA infra;      -- outbox e mecanismos técnicos
GO

/* ---------- 2. seg.Usuario ---------- */
CREATE TABLE seg.Usuario (
    Id                uniqueidentifier NOT NULL CONSTRAINT DF_Usuario_Id DEFAULT NEWSEQUENTIALID(),
    Nome              nvarchar(120)    NOT NULL,
    Email             nvarchar(254)    NOT NULL,
    -- [Atlas] Collation binária: CI_AI_SC do banco tornaria a unicidade acento-insensível
    -- (ex.: "jose@x.com" colidiria com "josé@x.com"), o que o ToUpperInvariant() da aplicação
    -- não cobre (só remove case, não acento). Unicidade exige comparação exata aqui.
    EmailNormalizado  nvarchar(254)    COLLATE Latin1_General_100_BIN2 NOT NULL,
    SenhaHash         nvarchar(512)    NOT NULL,
    Role              tinyint          NOT NULL,
    Ativo             bit              NOT NULL CONSTRAINT DF_Usuario_Ativo DEFAULT (1),
    -- [Atlas/v1.1] S-A12 (Sentinel, docs/specs/05-seguranca-auditoria.md): FalhasLogin e
    -- BloqueadoAteUtc saem do banco — bloqueio de login passa a ser por (conta, IpHash) no Redis.
    -- [Atlas/v1.1] S-B07: força troca de senha (senha provisória / reset administrativo).
    DeveTrocarSenha   bit              NOT NULL CONSTRAINT DF_Usuario_DeveTrocarSenha DEFAULT (0),
    CriadoEmUtc       datetime2(3)     NOT NULL CONSTRAINT DF_Usuario_CriadoEmUtc DEFAULT SYSUTCDATETIME(),
    AtualizadoEmUtc   datetime2(3)     NOT NULL CONSTRAINT DF_Usuario_AtualizadoEmUtc DEFAULT SYSUTCDATETIME(),
    RowVersion        rowversion       NOT NULL,
    CONSTRAINT PK_Usuario PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT CK_Usuario_Role CHECK (Role IN (1, 2, 3))               -- Admin, Locutor, Ouvinte
);
CREATE UNIQUE INDEX UX_Usuario_EmailNormalizado ON seg.Usuario (EmailNormalizado);
CREATE INDEX IX_Usuario_Role ON seg.Usuario (Role) INCLUDE (Nome, Ativo);
GO

/* ---------- 3. seg.RefreshToken ---------- */
CREATE TABLE seg.RefreshToken (
    Id                uniqueidentifier NOT NULL CONSTRAINT DF_RefreshToken_Id DEFAULT NEWSEQUENTIALID(),
    UsuarioId         uniqueidentifier NOT NULL,
    TokenHash         binary(32)       NOT NULL,     -- SHA-256 do token opaco
    FamiliaId         uniqueidentifier NOT NULL,     -- cadeia de rotação
    ExpiraEmUtc       datetime2(3)     NOT NULL,
    CriadoEmUtc       datetime2(3)     NOT NULL CONSTRAINT DF_RefreshToken_CriadoEmUtc DEFAULT SYSUTCDATETIME(),
    CriadoPorIpHash   binary(32)       NULL,
    RevogadoEmUtc     datetime2(3)     NULL,
    SubstituidoPorId  uniqueidentifier NULL,
    CONSTRAINT PK_RefreshToken PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_RefreshToken_Usuario FOREIGN KEY (UsuarioId) REFERENCES seg.Usuario (Id) ON DELETE CASCADE,
    CONSTRAINT FK_RefreshToken_SubstituidoPor FOREIGN KEY (SubstituidoPorId) REFERENCES seg.RefreshToken (Id),
    CONSTRAINT CK_RefreshToken_Expira CHECK (ExpiraEmUtc > CriadoEmUtc)
);
CREATE UNIQUE INDEX UX_RefreshToken_TokenHash ON seg.RefreshToken (TokenHash);
CREATE INDEX IX_RefreshToken_UsuarioId ON seg.RefreshToken (UsuarioId);
CREATE INDEX IX_RefreshToken_FamiliaId ON seg.RefreshToken (FamiliaId) WHERE RevogadoEmUtc IS NULL;
GO

/* ---------- 4. grade.Programa ---------- */
CREATE TABLE grade.Programa (
    Id                uniqueidentifier NOT NULL CONSTRAINT DF_Programa_Id DEFAULT NEWSEQUENTIALID(),
    Titulo            nvarchar(120)    NOT NULL,
    Descricao         nvarchar(1000)   NULL,
    LocutorId         uniqueidentifier NOT NULL,
    InicioUtc         datetime2(0)     NOT NULL,
    FimUtc            datetime2(0)     NOT NULL,
    Status            tinyint          NOT NULL CONSTRAINT DF_Programa_Status DEFAULT (1),
    CanceladoEmUtc    datetime2(3)     NULL,
    CriadoEmUtc       datetime2(3)     NOT NULL CONSTRAINT DF_Programa_CriadoEmUtc DEFAULT SYSUTCDATETIME(),
    AtualizadoEmUtc   datetime2(3)     NOT NULL CONSTRAINT DF_Programa_AtualizadoEmUtc DEFAULT SYSUTCDATETIME(),
    RowVersion        rowversion       NOT NULL,
    CONSTRAINT PK_Programa PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Programa_Locutor FOREIGN KEY (LocutorId) REFERENCES seg.Usuario (Id),
    CONSTRAINT CK_Programa_Status CHECK (Status IN (1, 2, 3, 4)),      -- Agendado, AoVivo, Finalizado, Cancelado
    CONSTRAINT CK_Programa_Intervalo CHECK (FimUtc > InicioUtc),
    -- Mínimo 5 min na criação; encerrar ao vivo pode encurtar (FimUtc = agora), por isso o piso é 1 s aqui.
    CONSTRAINT CK_Programa_DuracaoMax CHECK (DATEDIFF(MINUTE, InicioUtc, FimUtc) <= 720),
    CONSTRAINT CK_Programa_Cancelado CHECK (
        (Status = 4 AND CanceladoEmUtc IS NOT NULL) OR (Status <> 4 AND CanceladoEmUtc IS NULL)
    )
);
-- Consulta de grade por intervalo + checagem de sobreposição:
--   WHERE InicioUtc <  @Fim
--     AND InicioUtc >  DATEADD(HOUR, -12, @Inicio)   -- limite pela duração máxima → seek de faixa
--     AND FimUtc    >  @Inicio
--     AND Status IN (1, 2)
CREATE INDEX IX_Programa_Grade ON grade.Programa (InicioUtc) INCLUDE (FimUtc, Status, Titulo, LocutorId);
-- Transições do Worker (filtrado: só estados ativos, tabela quente pequena).
CREATE INDEX IX_Programa_Worker ON grade.Programa (Status, InicioUtc) INCLUDE (FimUtc) WHERE Status IN (1, 2);
CREATE INDEX IX_Programa_LocutorId ON grade.Programa (LocutorId);
GO

/* ---------- 5. midia.ArquivoMidia ---------- */
CREATE TABLE midia.ArquivoMidia (
    Id                  uniqueidentifier NOT NULL CONSTRAINT DF_ArquivoMidia_Id DEFAULT NEWSEQUENTIALID(),
    NomeOriginal        nvarchar(255)    NOT NULL,
    Titulo              nvarchar(200)    NULL,
    Artista             nvarchar(200)    NULL,
    TipoMidia           tinyint          NOT NULL,
    -- [Atlas] Bucket/ChaveStorage/MimeType* em collation binária: a chave de objeto no MinIO/S3
    -- é case-sensitive; herdar CI_AI_SC do banco poderia fazer o UNIQUE (Bucket, ChaveStorage)
    -- tratar duas chaves fisicamente distintas no storage como duplicata (ou aceitar como
    -- diferentes duas que o storage trataria como a mesma).
    Bucket              varchar(63)      COLLATE Latin1_General_100_BIN2 NOT NULL,
    ChaveStorage        varchar(512)     COLLATE Latin1_General_100_BIN2 NOT NULL,
    MimeTypeDeclarado   varchar(100)     COLLATE Latin1_General_100_BIN2 NOT NULL,
    MimeType            varchar(100)     COLLATE Latin1_General_100_BIN2 NULL,  -- detectado (magic bytes + ffprobe)
    TamanhoBytes        bigint           NOT NULL,
    -- [Atlas/v1.1] S-M02/S-M03 (Sentinel): hash do arquivo ORIGINAL (antes da recodificação) e
    -- ETag do objeto de upload, registrados no `concluir`. HashSHA256 abaixo continua sendo o
    -- hash da SAÍDA recodificada — o único usado no dedupe UX_ArquivoMidia_Hash_Aprovado.
    HashOriginalSHA256  binary(32)       NULL,
    EtagUpload          varchar(64)      COLLATE Latin1_General_100_BIN2 NULL,
    HashSHA256          binary(32)       NULL,
    DuracaoSegundos     decimal(9,3)     NULL,
    StatusSanitizacao   tinyint          NOT NULL CONSTRAINT DF_ArquivoMidia_Status DEFAULT (1),
    -- [Atlas/v1.1] S-M04: tentativas do worker de sanitização (retry com limite) e lease de
    -- quem está processando (EmAnalise), para evitar dois workers na mesma mídia.
    TentativasSanitizacao tinyint        NOT NULL CONSTRAINT DF_ArquivoMidia_TentativasSanitizacao DEFAULT (0),
    EmAnaliseDesdeUtc   datetime2(3)     NULL,
    MotivoRejeicao      nvarchar(500)    NULL,
    EnviadoPorUsuarioId uniqueidentifier NOT NULL,
    DataUploadUtc       datetime2(3)     NOT NULL CONSTRAINT DF_ArquivoMidia_DataUploadUtc DEFAULT SYSUTCDATETIME(),
    UploadExpiraEmUtc   datetime2(3)     NOT NULL,
    SanitizadoEmUtc     datetime2(3)     NULL,
    Ativo               bit              NOT NULL CONSTRAINT DF_ArquivoMidia_Ativo DEFAULT (1),
    RowVersion          rowversion       NOT NULL,
    CONSTRAINT PK_ArquivoMidia PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_ArquivoMidia_EnviadoPor FOREIGN KEY (EnviadoPorUsuarioId) REFERENCES seg.Usuario (Id),
    CONSTRAINT CK_ArquivoMidia_Tipo CHECK (TipoMidia IN (1, 2, 3)),                 -- Vinheta, Comercial, Musica
    CONSTRAINT CK_ArquivoMidia_Status CHECK (StatusSanitizacao IN (1, 2, 3, 4, 5, 6)),
    CONSTRAINT CK_ArquivoMidia_Bucket CHECK (Bucket IN ('quarentena', 'midia')),
    CONSTRAINT CK_ArquivoMidia_Tamanho CHECK (TamanhoBytes > 0 AND TamanhoBytes <= 262144000),  -- 250 MB
    CONSTRAINT CK_ArquivoMidia_Duracao CHECK (DuracaoSegundos IS NULL OR DuracaoSegundos > 0),
    CONSTRAINT CK_ArquivoMidia_MimeType CHECK (MimeType IS NULL OR MimeType IN ('audio/mpeg', 'audio/ogg', 'audio/wav', 'audio/flac')),
    -- [Atlas/v1.1] S-M04: janela de tentativas (10 = mesmo teto usado no outbox, por convenção).
    CONSTRAINT CK_ArquivoMidia_TentativasSanitizacao CHECK (TentativasSanitizacao BETWEEN 0 AND 10),
    -- [Atlas/v1.1] S-M04: EmAnalise (3) sempre tem lease registrado.
    CONSTRAINT CK_ArquivoMidia_EmAnalise CHECK (StatusSanitizacao <> 3 OR EmAnaliseDesdeUtc IS NOT NULL),
    -- Invariante do domínio: Aprovado ⇒ metadados de sanitização completos e arquivo fora da quarentena.
    CONSTRAINT CK_ArquivoMidia_AprovadoCompleto CHECK (
        StatusSanitizacao <> 4
        OR (MimeType IS NOT NULL AND HashSHA256 IS NOT NULL AND DuracaoSegundos IS NOT NULL
            AND SanitizadoEmUtc IS NOT NULL AND Bucket = 'midia')
    ),
    CONSTRAINT CK_ArquivoMidia_Musica_Titulo CHECK (TipoMidia <> 3 OR Titulo IS NOT NULL)
);
CREATE UNIQUE INDEX UX_ArquivoMidia_Objeto ON midia.ArquivoMidia (Bucket, ChaveStorage);
-- Deduplicação: um mesmo conteúdo (saída recodificada) só pode estar aprovado uma vez.
CREATE UNIQUE INDEX UX_ArquivoMidia_Hash_Aprovado ON midia.ArquivoMidia (HashSHA256)
    WHERE StatusSanitizacao = 4 AND HashSHA256 IS NOT NULL;
-- [Atlas/v1.1] S-M02: localizar reenvios do mesmo arquivo original antes da recodificação.
-- Não-único: o mesmo original pode passar por tentativas Rejeitado/Aprovado distintas no tempo.
CREATE INDEX IX_ArquivoMidia_HashOriginal ON midia.ArquivoMidia (HashOriginalSHA256) WHERE HashOriginalSHA256 IS NOT NULL;
-- Biblioteca / rotação do playout.
CREATE INDEX IX_ArquivoMidia_Biblioteca ON midia.ArquivoMidia (TipoMidia, StatusSanitizacao)
    INCLUDE (Titulo, Artista, DuracaoSegundos, Ativo);
-- Fila do Worker de sanitização e limpeza de uploads abandonados.
-- [Atlas/v1.1] S-M04: TentativasSanitizacao/EmAnaliseDesdeUtc incluídos — o worker decide
-- retry vs. lease expirado sem key lookup.
CREATE INDEX IX_ArquivoMidia_FilaSanitizacao ON midia.ArquivoMidia (StatusSanitizacao, DataUploadUtc)
    INCLUDE (UploadExpiraEmUtc, TentativasSanitizacao, EmAnaliseDesdeUtc) WHERE StatusSanitizacao IN (1, 2, 3);
CREATE INDEX IX_ArquivoMidia_EnviadoPor ON midia.ArquivoMidia (EnviadoPorUsuarioId);
GO

/* ---------- 6. interacao.PedidoMusica ---------- */
CREATE TABLE interacao.PedidoMusica (
    Id                    uniqueidentifier NOT NULL CONSTRAINT DF_PedidoMusica_Id DEFAULT NEWSEQUENTIALID(),
    ProgramaId            uniqueidentifier NULL,
    UsuarioId             uniqueidentifier NULL,
    NomeOuvinte           nvarchar(60)     NOT NULL,
    TituloMusica          nvarchar(200)    NOT NULL,
    Artista               nvarchar(200)    NOT NULL,
    Mensagem              nvarchar(280)    NULL,
    ListenerDeviceHash    binary(32)       NULL,     -- HMAC(pepper, X-Listener-Id); NULL após 30 dias (LGPD)
    ListenerIpHash        binary(32)       NULL,     -- HMAC(pepper, IP);             NULL após 30 dias (LGPD)
    Status                tinyint          NOT NULL CONSTRAINT DF_PedidoMusica_Status DEFAULT (1),
    MidiaId               uniqueidentifier NULL,
    ModeradoPorUsuarioId  uniqueidentifier NULL,
    ModeradoEmUtc         datetime2(3)     NULL,
    MotivoRejeicao        nvarchar(280)    NULL,
    EnviadoPlayoutEmUtc   datetime2(3)     NULL,
    TocadoEmUtc           datetime2(3)     NULL,
    CriadoEmUtc           datetime2(3)     NOT NULL CONSTRAINT DF_PedidoMusica_CriadoEmUtc DEFAULT SYSUTCDATETIME(),
    RowVersion            rowversion       NOT NULL,
    CONSTRAINT PK_PedidoMusica PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_PedidoMusica_Programa FOREIGN KEY (ProgramaId) REFERENCES grade.Programa (Id),
    CONSTRAINT FK_PedidoMusica_Usuario FOREIGN KEY (UsuarioId) REFERENCES seg.Usuario (Id),
    CONSTRAINT FK_PedidoMusica_Midia FOREIGN KEY (MidiaId) REFERENCES midia.ArquivoMidia (Id),
    CONSTRAINT FK_PedidoMusica_ModeradoPor FOREIGN KEY (ModeradoPorUsuarioId) REFERENCES seg.Usuario (Id),
    CONSTRAINT CK_PedidoMusica_Status CHECK (Status IN (1, 2, 3, 4, 5)),   -- Pendente, Aprovado, Rejeitado, Tocado, Expirado
    CONSTRAINT CK_PedidoMusica_Moderacao CHECK (
        Status NOT IN (2, 3, 4) OR (ModeradoPorUsuarioId IS NOT NULL AND ModeradoEmUtc IS NOT NULL)
    ),
    CONSTRAINT CK_PedidoMusica_Rejeicao CHECK (Status <> 3 OR MotivoRejeicao IS NOT NULL),
    CONSTRAINT CK_PedidoMusica_Tocado CHECK (Status <> 4 OR TocadoEmUtc IS NOT NULL)
);
-- Fila de moderação (predicado com CONSTANTE Status = 1 para o índice filtrado ser usado).
CREATE INDEX IX_PedidoMusica_Fila ON interacao.PedidoMusica (CriadoEmUtc)
    INCLUDE (NomeOuvinte, TituloMusica, Artista, Mensagem, ProgramaId) WHERE Status = 1;
-- Fila do playout: aprovados com mídia ainda não enviados ao Liquidsoap.
CREATE INDEX IX_PedidoMusica_Playout ON interacao.PedidoMusica (ModeradoEmUtc)
    INCLUDE (MidiaId) WHERE Status = 2 AND MidiaId IS NOT NULL AND EnviadoPlayoutEmUtc IS NULL;
-- Anti-spam no fallback SQL + "meus pedidos".
CREATE INDEX IX_PedidoMusica_Device ON interacao.PedidoMusica (ListenerDeviceHash, CriadoEmUtc DESC)
    WHERE ListenerDeviceHash IS NOT NULL;
CREATE INDEX IX_PedidoMusica_Ip ON interacao.PedidoMusica (ListenerIpHash, CriadoEmUtc DESC)
    WHERE ListenerIpHash IS NOT NULL;
CREATE INDEX IX_PedidoMusica_Usuario ON interacao.PedidoMusica (UsuarioId, CriadoEmUtc DESC) WHERE UsuarioId IS NOT NULL;
CREATE INDEX IX_PedidoMusica_ProgramaId ON interacao.PedidoMusica (ProgramaId) WHERE ProgramaId IS NOT NULL;
CREATE INDEX IX_PedidoMusica_MidiaId ON interacao.PedidoMusica (MidiaId) WHERE MidiaId IS NOT NULL;
GO

/* ---------- 7. interacao.Divulgacao ---------- */
CREATE TABLE interacao.Divulgacao (
    Id                  uniqueidentifier NOT NULL CONSTRAINT DF_Divulgacao_Id DEFAULT NEWSEQUENTIALID(),
    Titulo              nvarchar(120)    NOT NULL,
    Mensagem            nvarchar(1000)   NOT NULL,
    ImagemChaveStorage  varchar(512)     NULL,       -- bucket "publico"
    LinkDestino         nvarchar(2048)   NULL,
    Ativo               bit              NOT NULL CONSTRAINT DF_Divulgacao_Ativo DEFAULT (1),
    Prioridade          tinyint          NOT NULL CONSTRAINT DF_Divulgacao_Prioridade DEFAULT (50),
    InicioExibicaoUtc   datetime2(0)     NULL,
    FimExibicaoUtc      datetime2(0)     NULL,
    CriadoPorUsuarioId  uniqueidentifier NOT NULL,
    CriadoEmUtc         datetime2(3)     NOT NULL CONSTRAINT DF_Divulgacao_CriadoEmUtc DEFAULT SYSUTCDATETIME(),
    AtualizadoEmUtc     datetime2(3)     NOT NULL CONSTRAINT DF_Divulgacao_AtualizadoEmUtc DEFAULT SYSUTCDATETIME(),
    RowVersion          rowversion       NOT NULL,
    CONSTRAINT PK_Divulgacao PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Divulgacao_CriadoPor FOREIGN KEY (CriadoPorUsuarioId) REFERENCES seg.Usuario (Id),
    CONSTRAINT CK_Divulgacao_Prioridade CHECK (Prioridade BETWEEN 0 AND 100),
    CONSTRAINT CK_Divulgacao_Link CHECK (LinkDestino IS NULL OR LinkDestino LIKE 'https://%'),
    CONSTRAINT CK_Divulgacao_Janela CHECK (InicioExibicaoUtc IS NULL OR FimExibicaoUtc IS NULL OR FimExibicaoUtc > InicioExibicaoUtc)
);
CREATE INDEX IX_Divulgacao_Ativas ON interacao.Divulgacao (Prioridade DESC)
    INCLUDE (Titulo, Mensagem, ImagemChaveStorage, LinkDestino, InicioExibicaoUtc, FimExibicaoUtc) WHERE Ativo = 1;
GO

/* ---------- 8. midia.Reproducao (histórico de playout, só inserção) ---------- */
CREATE TABLE midia.Reproducao (
    Id             bigint IDENTITY(1,1) NOT NULL,
    MidiaId        uniqueidentifier     NOT NULL,
    PedidoId       uniqueidentifier     NULL,
    ProgramaId     uniqueidentifier     NULL,
    IniciadoEmUtc  datetime2(0)         NOT NULL,
    CONSTRAINT PK_Reproducao PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Reproducao_Midia FOREIGN KEY (MidiaId) REFERENCES midia.ArquivoMidia (Id),
    CONSTRAINT FK_Reproducao_Pedido FOREIGN KEY (PedidoId) REFERENCES interacao.PedidoMusica (Id),
    CONSTRAINT FK_Reproducao_Programa FOREIGN KEY (ProgramaId) REFERENCES grade.Programa (Id)
);
-- Relatório "mais tocadas no período" (Atlas: rel.usp_MaisTocadas @DeUtc, @AteUtc — Épico 3).
CREATE INDEX IX_Reproducao_Periodo ON midia.Reproducao (IniciadoEmUtc) INCLUDE (MidiaId);
CREATE INDEX IX_Reproducao_MidiaId ON midia.Reproducao (MidiaId);
GO

/* ---------- 9. infra.EventoOutbox ---------- */
CREATE TABLE infra.EventoOutbox (
    Id               bigint IDENTITY(1,1) NOT NULL,
    Tipo             varchar(100)         NOT NULL,
    Payload          nvarchar(max)        NOT NULL,
    CriadoEmUtc      datetime2(3)         NOT NULL CONSTRAINT DF_EventoOutbox_CriadoEmUtc DEFAULT SYSUTCDATETIME(),
    ProcessadoEmUtc  datetime2(3)         NULL,
    Tentativas       smallint             NOT NULL CONSTRAINT DF_EventoOutbox_Tentativas DEFAULT (0),
    UltimoErro       nvarchar(1000)       NULL,
    CONSTRAINT PK_EventoOutbox PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT CK_EventoOutbox_Payload CHECK (ISJSON(Payload) = 1)
);
-- Fila do OutboxDispatcher: pendentes com até 10 tentativas.
-- Leitura: UPDATE TOP(50) ... WITH (UPDLOCK, READPAST, ROWLOCK) WHERE ProcessadoEmUtc IS NULL AND Tentativas < 10
CREATE INDEX IX_EventoOutbox_Pendentes ON infra.EventoOutbox (Id) INCLUDE (Tentativas) WHERE ProcessadoEmUtc IS NULL;
GO

/* ---------- 10. Segurança de acesso (template; Atlas finaliza — tarefa E1-A02) ----------
   Três principals, nenhum sysadmin:
     webradio_migrator : db_ddladmin + db_datareader + db_datawriter  (só o serviço "migrator")
     webradio_app      : SELECT/INSERT/UPDATE/DELETE nos schemas + EXECUTE (API e Worker)
     webradio_relatorio: SELECT em midia/grade/interacao + EXECUTE no schema rel (futuro)

   CREATE LOGIN webradio_app WITH PASSWORD = '$(APP_DB_PASSWORD)', CHECK_POLICY = ON;
   CREATE USER  webradio_app FOR LOGIN webradio_app;
   GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::seg       TO webradio_app;
   GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::grade     TO webradio_app;
   GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::interacao TO webradio_app;
   GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::midia     TO webradio_app;
   GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::infra     TO webradio_app;
   -- sp_getapplock é público; não precisa de GRANT.
   --------------------------------------------------------------------------------------- */

/* ---------- 11. Rotinas de manutenção (Atlas, Épico 1 cria o esqueleto) ----------
   - LGPD: UPDATE interacao.PedidoMusica SET ListenerDeviceHash = NULL, ListenerIpHash = NULL
           WHERE CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME()) AND (ListenerDeviceHash IS NOT NULL OR ListenerIpHash IS NOT NULL);
           (em lotes de 5.000 para não escalar o lock)
   - Outbox: DELETE infra.EventoOutbox WHERE ProcessadoEmUtc < DATEADD(DAY, -7, SYSUTCDATETIME());
   - RefreshToken: DELETE seg.RefreshToken WHERE ExpiraEmUtc < DATEADD(DAY, -1, SYSUTCDATETIME());
   Executadas pelo Worker (job diário), não por SQL Agent: o SQL Server Linux em
   contêiner não é dependência de agendamento.
   --------------------------------------------------------------------------------------- */
