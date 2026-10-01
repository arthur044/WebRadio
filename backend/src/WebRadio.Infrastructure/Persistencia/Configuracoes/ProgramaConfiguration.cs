using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Grade;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Infrastructure.Persistencia.Configuracoes;

public sealed class ProgramaConfiguration : IEntityTypeConfiguration<Programa>
{
    public void Configure(EntityTypeBuilder<Programa> builder)
    {
        builder.ToTable("Programa", "grade", tb =>
        {
            tb.HasCheckConstraint("CK_Programa_Status", "Status IN (1, 2, 3, 4)");
            tb.HasCheckConstraint("CK_Programa_Intervalo", "FimUtc > InicioUtc");
            tb.HasCheckConstraint("CK_Programa_DuracaoMax", "DATEDIFF(MINUTE, InicioUtc, FimUtc) <= 720");
            tb.HasCheckConstraint(
                "CK_Programa_Cancelado",
                "(Status = 4 AND CanceladoEmUtc IS NOT NULL) OR (Status <> 4 AND CanceladoEmUtc IS NULL)");
        });

        builder.HasKey(p => p.Id).HasName("PK_Programa");

        builder.Property(p => p.Id)
            .HasDefaultValueSql("NEWSEQUENTIALID()")
            .HasDefaultConstraintName("DF_Programa_Id")
            .ValueGeneratedOnAdd();

        builder.Property(p => p.Titulo)
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(p => p.Descricao)
            .HasMaxLength(1000);

        builder.Property(p => p.InicioUtc)
            .HasColumnType("datetime2(0)");

        builder.Property(p => p.FimUtc)
            .HasColumnType("datetime2(0)");

        builder.Property(p => p.Status)
            .HasColumnType("tinyint")
            .HasDefaultValue(StatusPrograma.Agendado)
            .HasDefaultConstraintName("DF_Programa_Status");

        builder.Property(p => p.CanceladoEmUtc)
            .HasColumnType("datetime2(3)");

        builder.Property(p => p.CriadoEmUtc)
            .HasColumnType("datetime2(3)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .HasDefaultConstraintName("DF_Programa_CriadoEmUtc");

        builder.Property(p => p.AtualizadoEmUtc)
            .HasColumnType("datetime2(3)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .HasDefaultConstraintName("DF_Programa_AtualizadoEmUtc");

        builder.Property(p => p.RowVersion)
            .IsRowVersion();

        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(p => p.LocutorId)
            .HasConstraintName("FK_Programa_Locutor")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.InicioUtc)
            .IncludeProperties(p => new { p.FimUtc, p.Status, p.Titulo, p.LocutorId })
            .HasDatabaseName("IX_Programa_Grade");

        builder.HasIndex(p => new { p.Status, p.InicioUtc })
            .IncludeProperties(p => p.FimUtc)
            .HasFilter("Status IN (1, 2)")
            .HasDatabaseName("IX_Programa_Worker");

        builder.HasIndex(p => p.LocutorId)
            .HasDatabaseName("IX_Programa_LocutorId");

        builder.IgnorarEventosDominio();
    }
}
