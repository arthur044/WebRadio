using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebRadio.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class IndiceLimitesDeUploadPorUsuario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArquivoMidia_EnviadoPor",
                schema: "midia",
                table: "ArquivoMidia");

            migrationBuilder.CreateIndex(
                name: "IX_ArquivoMidia_EnviadoPor",
                schema: "midia",
                table: "ArquivoMidia",
                columns: new[] { "EnviadoPorUsuarioId", "DataUploadUtc" })
                .Annotation("SqlServer:Include", new[] { "StatusSanitizacao", "TamanhoBytes", "UploadExpiraEmUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArquivoMidia_EnviadoPor",
                schema: "midia",
                table: "ArquivoMidia");

            migrationBuilder.CreateIndex(
                name: "IX_ArquivoMidia_EnviadoPor",
                schema: "midia",
                table: "ArquivoMidia",
                column: "EnviadoPorUsuarioId");
        }
    }
}
