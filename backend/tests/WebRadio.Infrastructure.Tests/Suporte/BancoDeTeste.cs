using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WebRadio.Infrastructure.Persistencia;
using Xunit;

namespace WebRadio.Infrastructure.Tests.Suporte;

/// <summary>
/// SQL Server real para os testes de integração. Usa WEBRADIO_TEST_SQL (servidor, ex.: o do Testcontainers da CI) ou o
/// LocalDB da máquina. Cada classe de teste ganha um banco novo, migrado de verdade e apagado no fim. Sem servidor disponível,
/// os testes [SqlFact] são ignorados com o motivo.
/// </summary>
public sealed class BancoDeTeste : IAsyncLifetime
{
    private static readonly Lazy<string?> Servidor = new(DescobrirServidor);

    private readonly string _nome = $"WebRadioTest_{Guid.NewGuid():N}";

    public static bool Disponivel => Servidor.Value is not null;

    public string ConnectionString =>
        new SqlConnectionStringBuilder(Servidor.Value!) { InitialCatalog = _nome }.ConnectionString;

    public async Task InitializeAsync()
    {
        if (!Disponivel)
        {
            return;
        }

        await ExecutarNoMasterAsync($"CREATE DATABASE [{_nome}]");
        await using var db = CriarContexto();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Disponivel)
        {
            SqlConnection.ClearAllPools();
            await ExecutarNoMasterAsync($"ALTER DATABASE [{_nome}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_nome}]");
        }
    }

    public RadioDbContext CriarContexto() =>
        new(new DbContextOptionsBuilder<RadioDbContext>().UseSqlServer(ConnectionString).Options);

    private static async Task ExecutarNoMasterAsync(string sql)
    {
        await using var conexao = new SqlConnection(Servidor.Value);
        await conexao.OpenAsync();
        await using var comando = new SqlCommand(sql, conexao);
        await comando.ExecuteNonQueryAsync();
    }

    private static string? DescobrirServidor()
    {
        var candidatos = new[]
        {
            Environment.GetEnvironmentVariable("WEBRADIO_TEST_SQL"),
            "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;Initial Catalog=master;Connect Timeout=15;TrustServerCertificate=true",
        };

        foreach (var candidato in candidatos.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            try
            {
                using var conexao = new SqlConnection(candidato);
                conexao.Open();
                return candidato;
            }
            catch (SqlException)
            {
            }
        }

        return null;
    }
}

/// <summary>[Fact] que só roda com SQL Server disponível; senão aparece como ignorado, nunca como verde falso.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (!BancoDeTeste.Disponivel)
        {
            Skip = "SQL Server indisponível (defina WEBRADIO_TEST_SQL ou instale o LocalDB).";
        }
    }
}
