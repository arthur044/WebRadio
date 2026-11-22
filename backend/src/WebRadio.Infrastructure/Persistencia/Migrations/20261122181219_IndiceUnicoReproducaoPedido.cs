using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebRadio.Infrastructure.Persistencia.Migrations
{
    /// <summary>
    /// E4-A03 (#151, 08 E4-F08 v1.7): <c>UX_Reproducao_PedidoId</c> no lugar do <c>IX_Reproducao_PedidoId</c>. Antes de criar o único, apaga as duplicatas por <c>PedidoId</c> que um <c>FaixaIniciada</c> simultâneo
    /// possa ter gravado (fica a primeira por <c>IniciadoEmUtc, Id</c>) e imprime quantas apagou (<c>REPRODUCAO_DEDUP</c>; aparece no sqlcmd e no ddl-check, não no efbundle). Esperado: 0.
    /// </summary>
    public partial class IndiceUnicoReproducaoPedido : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DECLARE @reproducaoDuplicadas int;
                ;WITH d AS (
                    SELECT ROW_NUMBER() OVER (PARTITION BY PedidoId ORDER BY IniciadoEmUtc, Id) AS n
                    FROM midia.Reproducao
                    WHERE PedidoId IS NOT NULL)
                DELETE FROM d WHERE n > 1;
                SET @reproducaoDuplicadas = @@ROWCOUNT;
                PRINT N'REPRODUCAO_DEDUP: ' + CAST(@reproducaoDuplicadas AS nvarchar(20)) + N' linha(s) duplicada(s) por PedidoId apagada(s) em midia.Reproducao.';
                """);

            migrationBuilder.DropIndex(
                name: "IX_Reproducao_PedidoId",
                schema: "midia",
                table: "Reproducao");

            migrationBuilder.CreateIndex(
                name: "UX_Reproducao_PedidoId",
                schema: "midia",
                table: "Reproducao",
                column: "PedidoId",
                unique: true,
                filter: "PedidoId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Reproducao_PedidoId",
                schema: "midia",
                table: "Reproducao");

            migrationBuilder.CreateIndex(
                name: "IX_Reproducao_PedidoId",
                schema: "midia",
                table: "Reproducao",
                column: "PedidoId");
        }
    }
}
