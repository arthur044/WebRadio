using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebRadio.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class OutboxBackoffPorEvento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EventoOutbox_Pendentes",
                schema: "infra",
                table: "EventoOutbox");

            migrationBuilder.AddColumn<DateTime>(
                name: "ProximaTentativaEmUtc",
                schema: "infra",
                table: "EventoOutbox",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventoOutbox_Pendentes",
                schema: "infra",
                table: "EventoOutbox",
                column: "Id",
                filter: "ProcessadoEmUtc IS NULL AND Tentativas < 10")
                .Annotation("SqlServer:Include", new[] { "Tentativas", "ProximaTentativaEmUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EventoOutbox_Pendentes",
                schema: "infra",
                table: "EventoOutbox");

            migrationBuilder.DropColumn(
                name: "ProximaTentativaEmUtc",
                schema: "infra",
                table: "EventoOutbox");

            migrationBuilder.CreateIndex(
                name: "IX_EventoOutbox_Pendentes",
                schema: "infra",
                table: "EventoOutbox",
                column: "Id",
                filter: "ProcessadoEmUtc IS NULL")
                .Annotation("SqlServer:Include", new[] { "Tentativas" });
        }
    }
}
