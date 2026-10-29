using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebRadio.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class IndicePedidoAprovados : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PedidoMusica_Aprovados",
                schema: "interacao",
                table: "PedidoMusica",
                column: "ModeradoEmUtc",
                filter: "Status = 2")
                .Annotation("SqlServer:Include", new[] { "NomeOuvinte", "TituloMusica", "Artista", "Mensagem", "ProgramaId", "CriadoEmUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PedidoMusica_Aprovados",
                schema: "interacao",
                table: "PedidoMusica");
        }
    }
}
