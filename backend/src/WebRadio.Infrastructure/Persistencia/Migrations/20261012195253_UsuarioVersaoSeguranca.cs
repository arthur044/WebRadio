using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebRadio.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class UsuarioVersaoSeguranca : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VersaoSeguranca",
                schema: "seg",
                table: "Usuario",
                type: "int",
                nullable: false,
                defaultValue: 0)
                .Annotation("Relational:DefaultConstraintName", "DF_Usuario_VersaoSeguranca");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VersaoSeguranca",
                schema: "seg",
                table: "Usuario")
                .Annotation("Relational:DefaultConstraintName", "DF_Usuario_VersaoSeguranca");
        }
    }
}
