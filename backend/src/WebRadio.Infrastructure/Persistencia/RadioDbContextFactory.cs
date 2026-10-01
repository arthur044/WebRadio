using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WebRadio.Infrastructure.Persistencia;

/// <summary>
/// Só para design-time (`dotnet ef migrations add/script`). A connection string real vem de
/// AddInfrastructure(config) em runtime (Api/Worker/Migrator, F05/F06/F11) — aqui é só o suficiente
/// para o EF Core gerar SQL para SqlServer sem precisar conectar de verdade.
/// </summary>
public sealed class RadioDbContextFactory : IDesignTimeDbContextFactory<RadioDbContext>
{
    public RadioDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<RadioDbContext>();
        optionsBuilder.UseSqlServer("Server=localhost;Database=WebRadio;Trusted_Connection=True;TrustServerCertificate=True;");

        return new RadioDbContext(optionsBuilder.Options);
    }
}
