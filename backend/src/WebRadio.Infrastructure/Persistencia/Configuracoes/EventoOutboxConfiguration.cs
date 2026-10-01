using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebRadio.Infrastructure.Persistencia.Outbox;

namespace WebRadio.Infrastructure.Persistencia.Configuracoes;

public sealed class EventoOutboxConfiguration : IEntityTypeConfiguration<EventoOutbox>
{
    public void Configure(EntityTypeBuilder<EventoOutbox> builder)
    {
        builder.ToTable("EventoOutbox", "infra", t =>
            t.HasCheckConstraint("CK_EventoOutbox_Payload", "ISJSON(Payload) = 1"));

        builder.HasKey(e => e.Id).HasName("PK_EventoOutbox");

        builder.Property(e => e.Id)
            .UseIdentityColumn(1, 1);

        builder.Property(e => e.Tipo)
            .HasColumnType("varchar(100)");

        builder.Property(e => e.Payload)
            .HasColumnType("nvarchar(max)");

        builder.Property(e => e.CriadoEmUtc)
            .HasColumnType("datetime2(3)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .HasDefaultConstraintName("DF_EventoOutbox_CriadoEmUtc");

        builder.Property(e => e.ProcessadoEmUtc)
            .HasColumnType("datetime2(3)");

        builder.Property(e => e.Tentativas)
            .HasColumnType("smallint")
            .HasDefaultValue((short)0)
            .HasDefaultConstraintName("DF_EventoOutbox_Tentativas");

        builder.Property(e => e.UltimoErro)
            .HasColumnType("nvarchar(1000)");

        builder.HasIndex(e => e.Id)
            .IncludeProperties(e => e.Tentativas)
            .HasFilter("ProcessadoEmUtc IS NULL")
            .HasDatabaseName("IX_EventoOutbox_Pendentes");
    }
}
