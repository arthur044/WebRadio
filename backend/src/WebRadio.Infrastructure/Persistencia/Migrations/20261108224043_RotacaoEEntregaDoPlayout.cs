using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebRadio.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class RotacaoEEntregaDoPlayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PedidoMusica_Playout",
                schema: "interacao",
                table: "PedidoMusica");

            migrationBuilder.AddColumn<Guid>(
                name: "EnviadoPlayoutInstancia",
                schema: "interacao",
                table: "PedidoMusica",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Sorteio",
                schema: "midia",
                table: "ArquivoMidia",
                type: "int",
                nullable: false,
                computedColumnSql: "CONVERT(int, SUBSTRING(HASHBYTES('SHA2_256', CONVERT(binary(16), [Id])), 1, 4))",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_PedidoMusica_Playout",
                schema: "interacao",
                table: "PedidoMusica",
                column: "ModeradoEmUtc",
                filter: "Status = 2 AND MidiaId IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "MidiaId", "EnviadoPlayoutEmUtc", "EnviadoPlayoutInstancia" });

            migrationBuilder.CreateIndex(
                name: "IX_ArquivoMidia_Rotacao",
                schema: "midia",
                table: "ArquivoMidia",
                columns: new[] { "TipoMidia", "Sorteio" },
                filter: "StatusSanitizacao = 4 AND Ativo = 1")
                .Annotation("SqlServer:Include", new[] { "Titulo", "Artista" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PedidoMusica_Playout",
                schema: "interacao",
                table: "PedidoMusica");

            migrationBuilder.DropIndex(
                name: "IX_ArquivoMidia_Rotacao",
                schema: "midia",
                table: "ArquivoMidia");

            migrationBuilder.DropColumn(
                name: "Sorteio",
                schema: "midia",
                table: "ArquivoMidia");

            migrationBuilder.DropColumn(
                name: "EnviadoPlayoutInstancia",
                schema: "interacao",
                table: "PedidoMusica");

            migrationBuilder.CreateIndex(
                name: "IX_PedidoMusica_Playout",
                schema: "interacao",
                table: "PedidoMusica",
                column: "ModeradoEmUtc",
                filter: "Status = 2 AND MidiaId IS NOT NULL AND EnviadoPlayoutEmUtc IS NULL")
                .Annotation("SqlServer:Include", new[] { "MidiaId" });
        }
    }
}
