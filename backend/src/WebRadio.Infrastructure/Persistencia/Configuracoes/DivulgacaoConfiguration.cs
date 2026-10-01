using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebRadio.Domain.Interacao;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Infrastructure.Persistencia.Configuracoes;

public sealed class DivulgacaoConfiguration : IEntityTypeConfiguration<Divulgacao>
{
    public void Configure(EntityTypeBuilder<Divulgacao> builder)
    {
        builder.ToTable("Divulgacao", "interacao", tb =>
        {
            tb.HasCheckConstraint("CK_Divulgacao_Prioridade", "Prioridade BETWEEN 0 AND 100");
            tb.HasCheckConstraint("CK_Divulgacao_Link", "LinkDestino IS NULL OR LinkDestino LIKE 'https://%'");
            tb.HasCheckConstraint(
                "CK_Divulgacao_Janela",
                "InicioExibicaoUtc IS NULL OR FimExibicaoUtc IS NULL OR FimExibicaoUtc > InicioExibicaoUtc");
        });

        builder.HasKey(d => d.Id).HasName("PK_Divulgacao");

        builder.Property(d => d.Id)
            .HasDefaultValueSql("NEWSEQUENTIALID()")
            .HasDefaultConstraintName("DF_Divulgacao_Id")
            .ValueGeneratedOnAdd();

        builder.Property(d => d.Titulo).HasMaxLength(120).IsRequired();
        builder.Property(d => d.Mensagem).HasMaxLength(1000).IsRequired();
        builder.Property(d => d.ImagemChaveStorage).HasColumnType("varchar(512)");
        builder.Property(d => d.LinkDestino).HasMaxLength(2048);

        builder.Property(d => d.Ativo)
            .HasDefaultValue(true)
            .HasDefaultConstraintName("DF_Divulgacao_Ativo");

        builder.Property(d => d.Prioridade)
            .HasDefaultValue((byte)50)
            .HasDefaultConstraintName("DF_Divulgacao_Prioridade");

        builder.Property(d => d.InicioExibicaoUtc).HasColumnType("datetime2(0)");
        builder.Property(d => d.FimExibicaoUtc).HasColumnType("datetime2(0)");

        builder.Property(d => d.CriadoEmUtc)
            .HasColumnType("datetime2(3)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .HasDefaultConstraintName("DF_Divulgacao_CriadoEmUtc");

        builder.Property(d => d.AtualizadoEmUtc)
            .HasColumnType("datetime2(3)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .HasDefaultConstraintName("DF_Divulgacao_AtualizadoEmUtc");

        builder.Property(d => d.RowVersion).IsRowVersion();

        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(d => d.CriadoPorUsuarioId)
            .HasConstraintName("FK_Divulgacao_CriadoPor")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => d.Prioridade)
            .IsDescending()
            .IncludeProperties(d => new { d.Titulo, d.Mensagem, d.ImagemChaveStorage, d.LinkDestino, d.InicioExibicaoUtc, d.FimExibicaoUtc })
            .HasFilter("Ativo = 1")
            .HasDatabaseName("IX_Divulgacao_Ativas");

        builder.IgnorarEventosDominio();
    }
}
