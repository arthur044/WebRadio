using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebRadio.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class Inicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "midia");

            migrationBuilder.EnsureSchema(
                name: "interacao");

            migrationBuilder.EnsureSchema(
                name: "infra");

            migrationBuilder.EnsureSchema(
                name: "grade");

            migrationBuilder.EnsureSchema(
                name: "seg");

            migrationBuilder.CreateTable(
                name: "EventoOutbox",
                schema: "infra",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Tipo = table.Column<string>(type: "varchar(100)", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_EventoOutbox_CriadoEmUtc"),
                    ProcessadoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Tentativas = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0)
                        .Annotation("Relational:DefaultConstraintName", "DF_EventoOutbox_Tentativas"),
                    UltimoErro = table.Column<string>(type: "nvarchar(1000)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventoOutbox", x => x.Id);
                    table.CheckConstraint("CK_EventoOutbox_Payload", "ISJSON(Payload) = 1");
                });

            migrationBuilder.CreateTable(
                name: "Usuario",
                schema: "seg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Usuario_Id"),
                    Nome = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    EmailNormalizado = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SenhaHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Role = table.Column<byte>(type: "tinyint", nullable: false),
                    Ativo = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_Usuario_Ativo"),
                    DeveTrocarSenha = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_Usuario_DeveTrocarSenha"),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Usuario_CriadoEmUtc"),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Usuario_AtualizadoEmUtc"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Usuario", x => x.Id);
                    table.CheckConstraint("CK_Usuario_Role", "Role IN (1, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "ArquivoMidia",
                schema: "midia",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()")
                        .Annotation("Relational:DefaultConstraintName", "DF_ArquivoMidia_Id"),
                    NomeOriginal = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Titulo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Artista = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TipoMidia = table.Column<byte>(type: "tinyint", nullable: false),
                    Bucket = table.Column<string>(type: "varchar(63)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    ChaveStorage = table.Column<string>(type: "varchar(512)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    MimeTypeDeclarado = table.Column<string>(type: "varchar(100)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    MimeType = table.Column<string>(type: "varchar(100)", nullable: true, collation: "Latin1_General_100_BIN2"),
                    TamanhoBytes = table.Column<long>(type: "bigint", nullable: false),
                    HashSHA256 = table.Column<byte[]>(type: "binary(32)", nullable: true),
                    HashOriginalSHA256 = table.Column<byte[]>(type: "binary(32)", nullable: true),
                    EtagUpload = table.Column<string>(type: "varchar(64)", nullable: true, collation: "Latin1_General_100_BIN2"),
                    DuracaoSegundos = table.Column<decimal>(type: "decimal(9,3)", nullable: true),
                    StatusSanitizacao = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)1)
                        .Annotation("Relational:DefaultConstraintName", "DF_ArquivoMidia_Status"),
                    TentativasSanitizacao = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)0)
                        .Annotation("Relational:DefaultConstraintName", "DF_ArquivoMidia_TentativasSanitizacao"),
                    EmAnaliseDesdeUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    MotivoRejeicao = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    EnviadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataUploadUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_ArquivoMidia_DataUploadUtc"),
                    UploadExpiraEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    SanitizadoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Ativo = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_ArquivoMidia_Ativo"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArquivoMidia", x => x.Id);
                    table.CheckConstraint("CK_ArquivoMidia_AprovadoCompleto", "StatusSanitizacao <> 4 OR (MimeType IS NOT NULL AND HashSHA256 IS NOT NULL AND DuracaoSegundos IS NOT NULL AND SanitizadoEmUtc IS NOT NULL AND Bucket = 'midia')");
                    table.CheckConstraint("CK_ArquivoMidia_Bucket", "Bucket IN ('quarentena', 'midia')");
                    table.CheckConstraint("CK_ArquivoMidia_Duracao", "DuracaoSegundos IS NULL OR DuracaoSegundos > 0");
                    table.CheckConstraint("CK_ArquivoMidia_EmAnalise", "StatusSanitizacao <> 3 OR EmAnaliseDesdeUtc IS NOT NULL");
                    table.CheckConstraint("CK_ArquivoMidia_MimeType", "MimeType IS NULL OR MimeType IN ('audio/mpeg', 'audio/ogg', 'audio/wav', 'audio/flac')");
                    table.CheckConstraint("CK_ArquivoMidia_Musica_Titulo", "TipoMidia <> 3 OR Titulo IS NOT NULL");
                    table.CheckConstraint("CK_ArquivoMidia_Status", "StatusSanitizacao IN (1, 2, 3, 4, 5, 6)");
                    table.CheckConstraint("CK_ArquivoMidia_Tamanho", "TamanhoBytes > 0 AND TamanhoBytes <= 262144000");
                    table.CheckConstraint("CK_ArquivoMidia_TentativasSanitizacao", "TentativasSanitizacao BETWEEN 0 AND 10");
                    table.CheckConstraint("CK_ArquivoMidia_Tipo", "TipoMidia IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_ArquivoMidia_EnviadoPor",
                        column: x => x.EnviadoPorUsuarioId,
                        principalSchema: "seg",
                        principalTable: "Usuario",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Divulgacao",
                schema: "interacao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Divulgacao_Id"),
                    Titulo = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Mensagem = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ImagemChaveStorage = table.Column<string>(type: "varchar(512)", nullable: true),
                    LinkDestino = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    Ativo = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_Divulgacao_Ativo"),
                    Prioridade = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)50)
                        .Annotation("Relational:DefaultConstraintName", "DF_Divulgacao_Prioridade"),
                    InicioExibicaoUtc = table.Column<DateTime>(type: "datetime2(0)", nullable: true),
                    FimExibicaoUtc = table.Column<DateTime>(type: "datetime2(0)", nullable: true),
                    CriadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Divulgacao_CriadoEmUtc"),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Divulgacao_AtualizadoEmUtc"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Divulgacao", x => x.Id);
                    table.CheckConstraint("CK_Divulgacao_Janela", "InicioExibicaoUtc IS NULL OR FimExibicaoUtc IS NULL OR FimExibicaoUtc > InicioExibicaoUtc");
                    table.CheckConstraint("CK_Divulgacao_Link", "LinkDestino IS NULL OR LinkDestino LIKE 'https://%'");
                    table.CheckConstraint("CK_Divulgacao_Prioridade", "Prioridade BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "FK_Divulgacao_CriadoPor",
                        column: x => x.CriadoPorUsuarioId,
                        principalSchema: "seg",
                        principalTable: "Usuario",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Programa",
                schema: "grade",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Programa_Id"),
                    Titulo = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Descricao = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LocutorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InicioUtc = table.Column<DateTime>(type: "datetime2(0)", nullable: false),
                    FimUtc = table.Column<DateTime>(type: "datetime2(0)", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)1)
                        .Annotation("Relational:DefaultConstraintName", "DF_Programa_Status"),
                    CanceladoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Programa_CriadoEmUtc"),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Programa_AtualizadoEmUtc"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Programa", x => x.Id);
                    table.CheckConstraint("CK_Programa_Cancelado", "(Status = 4 AND CanceladoEmUtc IS NOT NULL) OR (Status <> 4 AND CanceladoEmUtc IS NULL)");
                    table.CheckConstraint("CK_Programa_DuracaoMax", "DATEDIFF(MINUTE, InicioUtc, FimUtc) <= 720");
                    table.CheckConstraint("CK_Programa_Intervalo", "FimUtc > InicioUtc");
                    table.CheckConstraint("CK_Programa_Status", "Status IN (1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_Programa_Locutor",
                        column: x => x.LocutorId,
                        principalSchema: "seg",
                        principalTable: "Usuario",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RefreshToken",
                schema: "seg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()")
                        .Annotation("Relational:DefaultConstraintName", "DF_RefreshToken_Id"),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    FamiliaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpiraEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_RefreshToken_CriadoEmUtc"),
                    CriadoPorIpHash = table.Column<byte[]>(type: "binary(32)", nullable: true),
                    RevogadoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SubstituidoPorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshToken", x => x.Id);
                    table.CheckConstraint("CK_RefreshToken_Expira", "ExpiraEmUtc > CriadoEmUtc");
                    table.ForeignKey(
                        name: "FK_RefreshToken_SubstituidoPor",
                        column: x => x.SubstituidoPorId,
                        principalSchema: "seg",
                        principalTable: "RefreshToken",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefreshToken_Usuario",
                        column: x => x.UsuarioId,
                        principalSchema: "seg",
                        principalTable: "Usuario",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PedidoMusica",
                schema: "interacao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()")
                        .Annotation("Relational:DefaultConstraintName", "DF_PedidoMusica_Id"),
                    ProgramaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NomeOuvinte = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    TituloMusica = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Artista = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Mensagem = table.Column<string>(type: "nvarchar(280)", maxLength: 280, nullable: true),
                    ListenerDeviceHash = table.Column<byte[]>(type: "binary(32)", nullable: true),
                    ListenerIpHash = table.Column<byte[]>(type: "binary(32)", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)1)
                        .Annotation("Relational:DefaultConstraintName", "DF_PedidoMusica_Status"),
                    MidiaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModeradoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModeradoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    MotivoRejeicao = table.Column<string>(type: "nvarchar(280)", maxLength: 280, nullable: true),
                    EnviadoPlayoutEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    TocadoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_PedidoMusica_CriadoEmUtc"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PedidoMusica", x => x.Id);
                    table.CheckConstraint("CK_PedidoMusica_Moderacao", "Status NOT IN (2, 3, 4) OR (ModeradoPorUsuarioId IS NOT NULL AND ModeradoEmUtc IS NOT NULL)");
                    table.CheckConstraint("CK_PedidoMusica_Rejeicao", "Status <> 3 OR MotivoRejeicao IS NOT NULL");
                    table.CheckConstraint("CK_PedidoMusica_Status", "Status IN (1, 2, 3, 4, 5)");
                    table.CheckConstraint("CK_PedidoMusica_Tocado", "Status <> 4 OR TocadoEmUtc IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_PedidoMusica_Midia",
                        column: x => x.MidiaId,
                        principalSchema: "midia",
                        principalTable: "ArquivoMidia",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PedidoMusica_ModeradoPor",
                        column: x => x.ModeradoPorUsuarioId,
                        principalSchema: "seg",
                        principalTable: "Usuario",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PedidoMusica_Programa",
                        column: x => x.ProgramaId,
                        principalSchema: "grade",
                        principalTable: "Programa",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PedidoMusica_Usuario",
                        column: x => x.UsuarioId,
                        principalSchema: "seg",
                        principalTable: "Usuario",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Reproducao",
                schema: "midia",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MidiaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PedidoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProgramaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IniciadoEmUtc = table.Column<DateTime>(type: "datetime2(0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reproducao", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reproducao_Midia",
                        column: x => x.MidiaId,
                        principalSchema: "midia",
                        principalTable: "ArquivoMidia",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Reproducao_Pedido",
                        column: x => x.PedidoId,
                        principalSchema: "interacao",
                        principalTable: "PedidoMusica",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Reproducao_Programa",
                        column: x => x.ProgramaId,
                        principalSchema: "grade",
                        principalTable: "Programa",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArquivoMidia_Biblioteca",
                schema: "midia",
                table: "ArquivoMidia",
                columns: new[] { "TipoMidia", "StatusSanitizacao" })
                .Annotation("SqlServer:Include", new[] { "Titulo", "Artista", "DuracaoSegundos", "Ativo" });

            migrationBuilder.CreateIndex(
                name: "IX_ArquivoMidia_EnviadoPor",
                schema: "midia",
                table: "ArquivoMidia",
                column: "EnviadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_ArquivoMidia_FilaSanitizacao",
                schema: "midia",
                table: "ArquivoMidia",
                columns: new[] { "StatusSanitizacao", "DataUploadUtc" },
                filter: "StatusSanitizacao IN (1, 2, 3)")
                .Annotation("SqlServer:Include", new[] { "UploadExpiraEmUtc", "TentativasSanitizacao", "EmAnaliseDesdeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ArquivoMidia_HashOriginal",
                schema: "midia",
                table: "ArquivoMidia",
                column: "HashOriginalSHA256",
                filter: "HashOriginalSHA256 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_ArquivoMidia_Hash_Aprovado",
                schema: "midia",
                table: "ArquivoMidia",
                column: "HashSHA256",
                unique: true,
                filter: "StatusSanitizacao = 4 AND HashSHA256 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_ArquivoMidia_Objeto",
                schema: "midia",
                table: "ArquivoMidia",
                columns: new[] { "Bucket", "ChaveStorage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Divulgacao_Ativas",
                schema: "interacao",
                table: "Divulgacao",
                column: "Prioridade",
                descending: new bool[0],
                filter: "Ativo = 1")
                .Annotation("SqlServer:Include", new[] { "Titulo", "Mensagem", "ImagemChaveStorage", "LinkDestino", "InicioExibicaoUtc", "FimExibicaoUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Divulgacao_CriadoPorUsuarioId",
                schema: "interacao",
                table: "Divulgacao",
                column: "CriadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_EventoOutbox_Pendentes",
                schema: "infra",
                table: "EventoOutbox",
                column: "Id",
                filter: "ProcessadoEmUtc IS NULL")
                .Annotation("SqlServer:Include", new[] { "Tentativas" });

            migrationBuilder.CreateIndex(
                name: "IX_PedidoMusica_Device",
                schema: "interacao",
                table: "PedidoMusica",
                columns: new[] { "ListenerDeviceHash", "CriadoEmUtc" },
                descending: new[] { false, true },
                filter: "ListenerDeviceHash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PedidoMusica_Fila",
                schema: "interacao",
                table: "PedidoMusica",
                column: "CriadoEmUtc",
                filter: "Status = 1")
                .Annotation("SqlServer:Include", new[] { "NomeOuvinte", "TituloMusica", "Artista", "Mensagem", "ProgramaId" });

            migrationBuilder.CreateIndex(
                name: "IX_PedidoMusica_Ip",
                schema: "interacao",
                table: "PedidoMusica",
                columns: new[] { "ListenerIpHash", "CriadoEmUtc" },
                descending: new[] { false, true },
                filter: "ListenerIpHash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PedidoMusica_MidiaId",
                schema: "interacao",
                table: "PedidoMusica",
                column: "MidiaId",
                filter: "MidiaId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PedidoMusica_ModeradoPorUsuarioId",
                schema: "interacao",
                table: "PedidoMusica",
                column: "ModeradoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_PedidoMusica_Playout",
                schema: "interacao",
                table: "PedidoMusica",
                column: "ModeradoEmUtc",
                filter: "Status = 2 AND MidiaId IS NOT NULL AND EnviadoPlayoutEmUtc IS NULL")
                .Annotation("SqlServer:Include", new[] { "MidiaId" });

            migrationBuilder.CreateIndex(
                name: "IX_PedidoMusica_ProgramaId",
                schema: "interacao",
                table: "PedidoMusica",
                column: "ProgramaId",
                filter: "ProgramaId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PedidoMusica_Usuario",
                schema: "interacao",
                table: "PedidoMusica",
                columns: new[] { "UsuarioId", "CriadoEmUtc" },
                descending: new[] { false, true },
                filter: "UsuarioId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Programa_Grade",
                schema: "grade",
                table: "Programa",
                column: "InicioUtc")
                .Annotation("SqlServer:Include", new[] { "FimUtc", "Status", "Titulo", "LocutorId" });

            migrationBuilder.CreateIndex(
                name: "IX_Programa_LocutorId",
                schema: "grade",
                table: "Programa",
                column: "LocutorId");

            migrationBuilder.CreateIndex(
                name: "IX_Programa_Worker",
                schema: "grade",
                table: "Programa",
                columns: new[] { "Status", "InicioUtc" },
                filter: "Status IN (1, 2)")
                .Annotation("SqlServer:Include", new[] { "FimUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshToken_FamiliaId",
                schema: "seg",
                table: "RefreshToken",
                column: "FamiliaId",
                filter: "RevogadoEmUtc IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshToken_SubstituidoPorId",
                schema: "seg",
                table: "RefreshToken",
                column: "SubstituidoPorId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshToken_UsuarioId",
                schema: "seg",
                table: "RefreshToken",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "UX_RefreshToken_TokenHash",
                schema: "seg",
                table: "RefreshToken",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reproducao_MidiaId",
                schema: "midia",
                table: "Reproducao",
                column: "MidiaId");

            migrationBuilder.CreateIndex(
                name: "IX_Reproducao_PedidoId",
                schema: "midia",
                table: "Reproducao",
                column: "PedidoId");

            migrationBuilder.CreateIndex(
                name: "IX_Reproducao_Periodo",
                schema: "midia",
                table: "Reproducao",
                column: "IniciadoEmUtc")
                .Annotation("SqlServer:Include", new[] { "MidiaId" });

            migrationBuilder.CreateIndex(
                name: "IX_Reproducao_ProgramaId",
                schema: "midia",
                table: "Reproducao",
                column: "ProgramaId");

            migrationBuilder.CreateIndex(
                name: "IX_Usuario_Role",
                schema: "seg",
                table: "Usuario",
                column: "Role")
                .Annotation("SqlServer:Include", new[] { "Nome", "Ativo" });

            migrationBuilder.CreateIndex(
                name: "UX_Usuario_EmailNormalizado",
                schema: "seg",
                table: "Usuario",
                column: "EmailNormalizado",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Divulgacao",
                schema: "interacao");

            migrationBuilder.DropTable(
                name: "EventoOutbox",
                schema: "infra");

            migrationBuilder.DropTable(
                name: "RefreshToken",
                schema: "seg");

            migrationBuilder.DropTable(
                name: "Reproducao",
                schema: "midia");

            migrationBuilder.DropTable(
                name: "PedidoMusica",
                schema: "interacao");

            migrationBuilder.DropTable(
                name: "ArquivoMidia",
                schema: "midia");

            migrationBuilder.DropTable(
                name: "Programa",
                schema: "grade");

            migrationBuilder.DropTable(
                name: "Usuario",
                schema: "seg");
        }
    }
}
