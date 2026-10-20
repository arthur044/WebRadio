using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebRadio.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class CredencialTransmissao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CredencialTransmissao",
                schema: "seg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()")
                        .Annotation("Relational:DefaultConstraintName", "DF_CredencialTransmissao_Id"),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Identificador = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SegredoHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    Salt = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    CriadaEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_CredencialTransmissao_CriadaEmUtc"),
                    RevogadaEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UltimoUsoEmUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CredencialTransmissao", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CredencialTransmissao_Usuario",
                        column: x => x.UsuarioId,
                        principalSchema: "seg",
                        principalTable: "Usuario",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_CredencialTransmissao_Ativa",
                schema: "seg",
                table: "CredencialTransmissao",
                column: "UsuarioId",
                unique: true,
                filter: "[RevogadaEmUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CredencialTransmissao_Identificador",
                schema: "seg",
                table: "CredencialTransmissao",
                column: "Identificador",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CredencialTransmissao",
                schema: "seg");
        }
    }
}
