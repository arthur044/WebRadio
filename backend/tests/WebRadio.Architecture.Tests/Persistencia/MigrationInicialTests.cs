using Xunit;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using WebRadio.Infrastructure.Persistencia.Migrations;

namespace WebRadio.Architecture.Tests.Persistencia;

/// <summary>
/// Guarda do PR #5: o `migrations add` já gerou uma Inicial com Up()/Down() vazios (Designer/Snapshot corretos,
/// DDL ausente). Roda offline — só inspeciona as operações, sem SQL Server.
/// </summary>
public sealed class MigrationInicialTests
{
    private static readonly string[] SchemasEsperados = ["seg", "grade", "interacao", "midia", "infra"];

    private static readonly string[] TabelasEsperadas =
    [
        "seg.Usuario", "seg.RefreshToken", "grade.Programa", "interacao.PedidoMusica",
        "interacao.Divulgacao", "midia.ArquivoMidia", "midia.Reproducao", "infra.EventoOutbox",
    ];

    private readonly Inicial _migration = new();

    [Fact]
    public void Up_cria_os_cinco_schemas()
    {
        var schemas = _migration.UpOperations.OfType<EnsureSchemaOperation>().Select(o => o.Name);

        Assert.Equal(SchemasEsperados.Order(), schemas.Order());
    }

    [Fact]
    public void Up_cria_todas_as_tabelas_do_schema_sql()
    {
        var tabelas = _migration.UpOperations.OfType<CreateTableOperation>().Select(o => $"{o.Schema}.{o.Name}");

        Assert.Equal(TabelasEsperadas.Order(), tabelas.Order());
    }

    [Fact]
    public void Down_desfaz_todas_as_tabelas()
    {
        var tabelas = _migration.DownOperations.OfType<DropTableOperation>().Select(o => $"{o.Schema}.{o.Name}");

        Assert.Equal(TabelasEsperadas.Order(), tabelas.Order());
    }
}
