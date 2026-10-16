using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Autenticacao;
using WebRadio.Domain.Grade;
using WebRadio.Domain.Interacao;
using WebRadio.Domain.Midia;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Persistencia.Outbox;

namespace WebRadio.Infrastructure.Persistencia;

/// <summary>E1-F04. Fiel a docs/specs/03-schema-sqlserver.sql (D14: as migrations são a fonte executável).</summary>
public sealed class RadioDbContext(DbContextOptions<RadioDbContext> options) : DbContext(options), IRadioDbContext
{
    public DbSet<Usuario> Usuarios => Set<Usuario>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Programa> Programas => Set<Programa>();

    public DbSet<PedidoMusica> PedidosMusica => Set<PedidoMusica>();

    public DbSet<ArquivoMidia> ArquivosMidia => Set<ArquivoMidia>();

    public DbSet<Divulgacao> Divulgacoes => Set<Divulgacao>();

    public DbSet<Reproducao> Reproducoes => Set<Reproducao>();

    public DbSet<EventoOutbox> EventosOutbox => Set<EventoOutbox>();

    /// <summary>
    /// Tudo no banco é UTC (02 §4, <c>UtcGuard</c> no Domain), mas o EF materializa <c>datetime2</c> como <c>Unspecified</c>, e o
    /// System.Text.Json só escreve o sufixo Z para <c>Kind = Utc</c>: o JSON saía sem o Z e o navegador lia hora local. Estes
    /// conversores marcam TODO <c>DateTime</c> e <c>DateTime?</c> (de hoje e de qualquer entidade futura) como UTC na leitura e não
    /// mudam o valor gravado: o esquema (e o <c>ddl-check</c>) não muda.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<DataUtc>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<DataUtcNulavel>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RadioDbContext).Assembly);
    }
}

/// <summary>Grava o valor como está e lê com <c>Kind = Utc</c>.</summary>
public sealed class DataUtc() : ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

/// <summary>O mesmo para <c>DateTime?</c>: nulo continua nulo.</summary>
public sealed class DataUtcNulavel() : ValueConverter<DateTime?, DateTime?>(
    v => v,
    v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
