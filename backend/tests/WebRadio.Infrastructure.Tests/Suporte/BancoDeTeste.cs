using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WebRadio.Infrastructure.Persistencia;
using Xunit;

namespace WebRadio.Infrastructure.Tests.Suporte;

/// <summary>
/// SQL Server real para os testes que dependem de UPDATE condicional, transação e applock (o InMemory não os suporta).
/// Usa WEBRADIO_TEST_SQL (servidor, ex.: o do Testcontainers da CI) ou o LocalDB da máquina. Cada classe ganha um banco novo,
/// migrado de verdade e apagado no fim. Sem servidor, os testes [SqlFact] aparecem como ignorados com o motivo.
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
        if (!Disponivel) return;

        await ExecutarNoMasterAsync($"CREATE DATABASE [{_nome}]");
        await using var db = CriarContexto();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (!Disponivel) return;

        SqlConnection.ClearAllPools();
        await ExecutarNoMasterAsync($"ALTER DATABASE [{_nome}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_nome}]");
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

    private static string? DescobrirServidor() => EscolherServidor(
        Environment.GetEnvironmentVariable("WEBRADIO_TEST_SQL"),
        // LocalDB só existe no Windows: no runner Linux o SqlClient lança PlatformNotSupportedException (não SqlException).
        OperatingSystem.IsWindows()
            ? "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;Initial Catalog=master;Connect Timeout=15;TrustServerCertificate=true"
            : null,
        static cs =>
        {
            using var conexao = new SqlConnection(cs);
            conexao.Open();
        });

    /// <summary>
    /// Com WEBRADIO_TEST_SQL definida o servidor é OBRIGATÓRIO: erro de conexão propaga e os [SqlFact] FALHAM (um job de
    /// CI com o SQL fora do ar não pode ficar verde só com testes ignorados). Só a sonda do LocalDB, que é opcional,
    /// engole o erro e deixa os testes pulados.
    /// </summary>
    internal static string? EscolherServidor(string? configurado, string? localDb, Action<string> abrir)
    {
        if (!string.IsNullOrWhiteSpace(configurado))
        {
            try
            {
                abrir(configurado);
            }
            catch (Exception e)
            {
                // A mensagem do SqlClient não traz a senha; a connection string em si nunca entra aqui.
                throw new InvalidOperationException($"WEBRADIO_TEST_SQL está definida, mas o servidor não respondeu: {e.Message}", e);
            }

            return configurado;
        }

        if (localDb is null) return null;

        try
        {
            abrir(localDb);
            return localDb;
        }
        catch (Exception e) when (e is SqlException or PlatformNotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>[Fact] que só roda com SQL Server disponível; senão aparece como ignorado, nunca como verde falso.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (!BancoDeTeste.Disponivel) Skip = "SQL Server indisponível (defina WEBRADIO_TEST_SQL ou instale o LocalDB).";
    }
}
