using Microsoft.EntityFrameworkCore;
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RadioDbContext).Assembly);
    }
}
