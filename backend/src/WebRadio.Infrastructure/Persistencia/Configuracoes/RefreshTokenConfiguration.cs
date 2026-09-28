using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebRadio.Domain.Autenticacao;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Infrastructure.Persistencia.Configuracoes;

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshToken", "seg", tb =>
            tb.HasCheckConstraint("CK_RefreshToken_Expira", "ExpiraEmUtc > CriadoEmUtc"));

        builder.HasKey(t => t.Id).HasName("PK_RefreshToken");

        builder.Property(t => t.Id)
            .HasDefaultValueSql("NEWSEQUENTIALID()")
            .HasDefaultConstraintName("DF_RefreshToken_Id")
            .ValueGeneratedOnAdd();

        builder.Property(t => t.TokenHash)
            .HasColumnType("binary(32)")
            .IsRequired();

        builder.Property(t => t.CriadoPorIpHash)
            .HasColumnType("binary(32)");

        builder.Property(t => t.ExpiraEmUtc)
            .HasColumnType("datetime2(3)");

        builder.Property(t => t.CriadoEmUtc)
            .HasColumnType("datetime2(3)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .HasDefaultConstraintName("DF_RefreshToken_CriadoEmUtc");

        builder.Property(t => t.RevogadoEmUtc)
            .HasColumnType("datetime2(3)");

        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(t => t.UsuarioId)
            .HasConstraintName("FK_RefreshToken_Usuario")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<RefreshToken>()
            .WithMany()
            .HasForeignKey(t => t.SubstituidoPorId)
            .HasConstraintName("FK_RefreshToken_SubstituidoPor")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => t.TokenHash)
            .IsUnique()
            .HasDatabaseName("UX_RefreshToken_TokenHash");

        builder.HasIndex(t => t.UsuarioId)
            .HasDatabaseName("IX_RefreshToken_UsuarioId");

        builder.HasIndex(t => t.FamiliaId)
            .HasFilter("RevogadoEmUtc IS NULL")
            .HasDatabaseName("IX_RefreshToken_FamiliaId");

        // schema §2.2: sem RowVersion (não há coluna em seg.RefreshToken).
        builder.Ignore(t => t.RowVersion);
        builder.IgnorarEventosDominio();
    }
}
