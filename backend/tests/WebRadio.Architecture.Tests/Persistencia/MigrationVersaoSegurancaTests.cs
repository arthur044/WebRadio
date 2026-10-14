using Microsoft.EntityFrameworkCore.Migrations.Operations;
using WebRadio.Infrastructure.Persistencia.Migrations;
using Xunit;

namespace WebRadio.Architecture.Tests.Persistencia;

/// <summary>
/// Mesma guarda da Inicial: a migration precisa trazer o DDL (o `migrations add` já gerou Up()/Down() vazios). A coluna é
/// NOT NULL com DEFAULT 0, para as linhas que já existem ganharem a versão 0 que os tokens novos carregam.
/// </summary>
public sealed class MigrationVersaoSegurancaTests
{
    private readonly UsuarioVersaoSeguranca _migration = new();

    [Fact]
    public void Up_adiciona_seg_Usuario_VersaoSeguranca_int_not_null_default_0()
    {
        var coluna = Assert.Single(_migration.UpOperations.OfType<AddColumnOperation>());

        Assert.Equal(("seg", "Usuario", "VersaoSeguranca"), (coluna.Schema, coluna.Table, coluna.Name));
        Assert.Equal("int", coluna.ColumnType);
        Assert.False(coluna.IsNullable);
        Assert.Equal(0, coluna.DefaultValue);
        Assert.Single(_migration.UpOperations);
    }

    [Fact]
    public void Down_remove_so_a_coluna()
    {
        var coluna = Assert.Single(_migration.DownOperations.OfType<DropColumnOperation>());

        Assert.Equal(("seg", "Usuario", "VersaoSeguranca"), (coluna.Schema, coluna.Table, coluna.Name));
        Assert.Single(_migration.DownOperations);
    }
}
