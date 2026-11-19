using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebRadio.Infrastructure.Persistencia.Migrations
{
    /// <summary>
    /// E4-A02 (08, E4-D12): <c>rel.usp_MaisTocadas</c>. O schema <c>rel</c> nasce no <c>infra/sql/init-db.sql</c> (o <c>logins.sql</c> concede
    /// EXECUTE nele antes de o migrator existir); o <c>EnsureSchema</c> só cobre um banco que não passou pelo db-init (o <c>ddl-check</c>).
    /// O texto da procedure é o mesmo do <c>docs/specs/03-schema-sqlserver.sql</c> (seção 9.1): o <c>ddl-check</c> compara o hash dos dois,
    /// então mude os dois juntos.
    /// </summary>
    public partial class RelatorioMaisTocadas : Migration
    {
        // CREATE PROCEDURE tem de ser o único comando do lote; dentro do script idempotente do EF isso exige EXEC. As aspas são dobradas aqui.
        private const string Procedure = """
            CREATE PROCEDURE rel.usp_MaisTocadas
                @DeUtc  datetime2(0),
                @AteUtc datetime2(0),
                @Top    int
            AS
            BEGIN
                SET NOCOUNT ON;

                IF @DeUtc IS NULL OR @AteUtc IS NULL OR @AteUtc <= @DeUtc
                    THROW 50101, N'Período inválido: @DeUtc e @AteUtc são obrigatórios e @DeUtc deve ser anterior a @AteUtc.', 1;
                IF @AteUtc > DATEADD(DAY, 366, @DeUtc)
                    THROW 50102, N'Período inválido: o máximo é de 366 dias.', 1;
                IF @Top IS NULL OR @Top < 1 OR @Top > 100
                    THROW 50103, N'@Top inválido: use um valor de 1 a 100.', 1;

                SELECT m.Id AS MidiaId, m.Titulo, m.Artista, m.TipoMidia, t.Reproducoes
                FROM (SELECT TOP (@Top) r.MidiaId, COUNT(*) AS Reproducoes
                      FROM midia.Reproducao AS r
                      WHERE r.IniciadoEmUtc >= @DeUtc AND r.IniciadoEmUtc < @AteUtc
                      GROUP BY r.MidiaId
                      ORDER BY COUNT(*) DESC, r.MidiaId) AS t
                JOIN midia.ArquivoMidia AS m ON m.Id = t.MidiaId
                ORDER BY t.Reproducoes DESC, t.MidiaId
                OPTION (RECOMPILE);
            END
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "rel");

            migrationBuilder.Sql($"EXEC(N'{Procedure.Replace("'", "''")}');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE rel.usp_MaisTocadas;");
        }
    }
}
