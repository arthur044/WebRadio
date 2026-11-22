using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebRadio.Infrastructure.Persistencia.Migrations
{
    /// <summary>
    /// #18: teto absoluto de 30 dias da família de refresh. <c>seg.RefreshToken.FamiliaCriadaEmUtc</c> (NOT NULL, backfill com o início da família) e <c>CK_RefreshToken_Familia</c>.
    /// O <c>docs/specs/03-schema-sqlserver.sql</c> muda junto (o <c>ddl-check</c> compara os dois).
    /// </summary>
    public partial class RefreshTokenTetoDaFamilia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nasce NULL para o backfill; vira NOT NULL logo depois (nenhuma linha fica sem a data da família).
            migrationBuilder.AddColumn<DateTime>(
                name: "FamiliaCriadaEmUtc",
                schema: "seg",
                table: "RefreshToken",
                type: "datetime2(3)",
                nullable: true);

            // Linhas anteriores à coluna: o MIN do CriadoEmUtc da FamiliaId, que é <= CriadoEmUtc de cada linha (o CK nasce válido). ATENÇÃO: o MIN só enxerga as linhas
            // RETIDAS (a Manutencao apaga o que venceu há mais de 1 dia), então NÃO é a data real do login: ele subestima a idade de uma família antiga (100 dias, rotação a cada
            // 7, resta ~1 semana de linhas). Efeito: essa família ganha no máximo 30 dias a partir do deploy (nunca é recusada antes da hora, nenhuma passa disso). Não há dado
            // melhor. Para as famílias novas não há erro: o sucessor herda o valor do pai. EXEC porque, no script idempotente, o lote precisa enxergar a coluna recém-criada.
            migrationBuilder.Sql(
                "EXEC(N'UPDATE t SET FamiliaCriadaEmUtc = f.Inicio FROM seg.RefreshToken AS t JOIN (SELECT FamiliaId, MIN(CriadoEmUtc) AS Inicio FROM seg.RefreshToken GROUP BY FamiliaId) AS f ON f.FamiliaId = t.FamiliaId');");

            migrationBuilder.AlterColumn<DateTime>(
                name: "FamiliaCriadaEmUtc",
                schema: "seg",
                table: "RefreshToken",
                type: "datetime2(3)",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RefreshToken_Familia",
                schema: "seg",
                table: "RefreshToken",
                sql: "FamiliaCriadaEmUtc <= CriadoEmUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RefreshToken_Familia",
                schema: "seg",
                table: "RefreshToken");

            migrationBuilder.DropColumn(
                name: "FamiliaCriadaEmUtc",
                schema: "seg",
                table: "RefreshToken");
        }
    }
}
