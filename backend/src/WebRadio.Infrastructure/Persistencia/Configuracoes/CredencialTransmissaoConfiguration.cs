using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebRadio.Domain.Transmissao;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Infrastructure.Persistencia.Configuracoes;

/// <summary>03 §3.1 (S-A08, revisado pelo Atlas no PR #38). Sem CHECK entre as datas: CriadaEmUtc e RevogadaEmUtc vêm de relógios diferentes.</summary>
public sealed class CredencialTransmissaoConfiguration : IEntityTypeConfiguration<CredencialTransmissao>
{
    public void Configure(EntityTypeBuilder<CredencialTransmissao> builder)
    {
        builder.ToTable("CredencialTransmissao", "seg");

        builder.HasKey(c => c.Id).HasName("PK_CredencialTransmissao");

        builder.Property(c => c.Id)
            .HasDefaultValueSql("NEWSEQUENTIALID()")
            .HasDefaultConstraintName("DF_CredencialTransmissao_Id")
            .ValueGeneratedOnAdd();

        builder.Property(c => c.SegredoHash)
            .HasColumnType("binary(32)")
            .IsRequired();

        builder.Property(c => c.Salt)
            .HasColumnType("binary(16)")
            .IsRequired();

        builder.Property(c => c.CriadaEmUtc)
            .HasColumnType("datetime2(3)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .HasDefaultConstraintName("DF_CredencialTransmissao_CriadaEmUtc");

        builder.Property(c => c.RevogadaEmUtc)
            .HasColumnType("datetime2(3)");

        builder.Property(c => c.UltimoUsoEmUtc)
            .HasColumnType("datetime2(3)");

        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(c => c.UsuarioId)
            .HasConstraintName("FK_CredencialTransmissao_Usuario")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => c.Identificador)
            .IsUnique()
            .HasDatabaseName("UX_CredencialTransmissao_Identificador");

        // Uma credencial ativa por locutor. Também cobre a FK (UsuarioId à esquerda): o EF não cria um IX_ separado.
        builder.HasIndex(c => c.UsuarioId)
            .IsUnique()
            .HasFilter("[RevogadaEmUtc] IS NULL")
            .HasDatabaseName("UX_CredencialTransmissao_Ativa");

        builder.Ignore(c => c.EstaAtiva);
        // Sem RowVersion (não há coluna em seg.CredencialTransmissao).
        builder.Ignore(c => c.RowVersion);
        builder.IgnorarEventosDominio();
    }
}
