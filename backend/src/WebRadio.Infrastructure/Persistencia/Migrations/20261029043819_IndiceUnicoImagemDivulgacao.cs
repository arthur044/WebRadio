using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebRadio.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class IndiceUnicoImagemDivulgacao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_Divulgacao_ImagemChave",
                schema: "interacao",
                table: "Divulgacao",
                column: "ImagemChaveStorage",
                unique: true,
                filter: "ImagemChaveStorage IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Divulgacao_ImagemChave",
                schema: "interacao",
                table: "Divulgacao");
        }
    }
}
