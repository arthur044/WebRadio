using Microsoft.EntityFrameworkCore.Migrations.Operations;
using WebRadio.Infrastructure.Persistencia.Migrations;
using Xunit;

namespace WebRadio.Architecture.Tests.Persistencia;

/// <summary>03 §3.1: a migration traz a tabela e os dois índices únicos com os nomes do contrato (o ddl-check compara o resto).</summary>
public sealed class MigrationCredencialTransmissaoTests
{
    private readonly CredencialTransmissao _migration = new();

    [Fact]
    public void Up_cria_seg_CredencialTransmissao_com_fk_em_cascata()
    {
        var tabela = Assert.Single(_migration.UpOperations.OfType<CreateTableOperation>());

        Assert.Equal(("seg", "CredencialTransmissao"), (tabela.Schema, tabela.Name));
        var fk = Assert.Single(tabela.ForeignKeys);
        Assert.Equal("FK_CredencialTransmissao_Usuario", fk.Name);
        Assert.Equal(Microsoft.EntityFrameworkCore.Migrations.ReferentialAction.Cascade, fk.OnDelete);
        Assert.Equal("binary(32)", tabela.Columns.Single(c => c.Name == "SegredoHash").ColumnType);
        Assert.Equal("binary(16)", tabela.Columns.Single(c => c.Name == "Salt").ColumnType);
    }

    [Fact]
    public void Up_cria_so_os_dois_indices_unicos_do_contrato()
    {
        var indices = _migration.UpOperations.OfType<CreateIndexOperation>().OrderBy(i => i.Name).ToList();

        Assert.Equal(["UX_CredencialTransmissao_Ativa", "UX_CredencialTransmissao_Identificador"], indices.Select(i => i.Name));
        Assert.All(indices, i => Assert.True(i.IsUnique));
        Assert.Equal("[RevogadaEmUtc] IS NULL", indices[0].Filter);
        Assert.Equal(["UsuarioId"], indices[0].Columns);
    }

    [Fact]
    public void Down_apaga_so_a_tabela()
    {
        var drop = Assert.Single(_migration.DownOperations);

        Assert.Equal("CredencialTransmissao", Assert.IsType<DropTableOperation>(drop).Name);
    }
}
