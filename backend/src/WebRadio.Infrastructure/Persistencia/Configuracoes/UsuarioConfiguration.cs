using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Infrastructure.Persistencia.Configuracoes;

public sealed class UsuarioConfiguration : IEntityTypeConfiguration<Usuario>
{
    public void Configure(EntityTypeBuilder<Usuario> builder)
    {
        builder.ToTable("Usuario", "seg", tb =>
            tb.HasCheckConstraint("CK_Usuario_Role", "Role IN (1, 2, 3)"));

        builder.HasKey(u => u.Id).HasName("PK_Usuario");

        builder.Property(u => u.Id)
            .HasDefaultValueSql("NEWSEQUENTIALID()")
            .HasDefaultConstraintName("DF_Usuario_Id")
            .ValueGeneratedOnAdd();

        builder.Property(u => u.Nome)
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(u => u.Email)
            .HasMaxLength(254)
            .IsRequired();

        builder.Property(u => u.EmailNormalizado)
            .HasMaxLength(254)
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired();

        builder.Property(u => u.SenhaHash)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(u => u.Role)
            .HasColumnType("tinyint");

        builder.Property(u => u.Ativo)
            .HasDefaultValue(true)
            .HasDefaultConstraintName("DF_Usuario_Ativo");

        builder.Property(u => u.DeveTrocarSenha)
            .HasDefaultValue(false)
            .HasDefaultConstraintName("DF_Usuario_DeveTrocarSenha");

        builder.Property(u => u.CriadoEmUtc)
            .HasColumnType("datetime2(3)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .HasDefaultConstraintName("DF_Usuario_CriadoEmUtc");

        builder.Property(u => u.AtualizadoEmUtc)
            .HasColumnType("datetime2(3)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .HasDefaultConstraintName("DF_Usuario_AtualizadoEmUtc");

        builder.Property(u => u.RowVersion)
            .IsRowVersion();

        builder.HasIndex(u => u.EmailNormalizado)
            .IsUnique()
            .HasDatabaseName("UX_Usuario_EmailNormalizado");

        builder.HasIndex(u => u.Role)
            .IncludeProperties(u => new { u.Nome, u.Ativo })
            .HasDatabaseName("IX_Usuario_Role");

        builder.IgnorarEventosDominio();
    }
}
