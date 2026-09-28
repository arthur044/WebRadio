using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebRadio.Domain.Grade;
using WebRadio.Domain.Interacao;
using WebRadio.Domain.Midia;

namespace WebRadio.Infrastructure.Persistencia.Configuracoes;

public sealed class ReproducaoConfiguration : IEntityTypeConfiguration<Reproducao>
{
    public void Configure(EntityTypeBuilder<Reproducao> builder)
    {
        builder.ToTable("Reproducao", "midia");

        builder.HasKey(r => r.Id).HasName("PK_Reproducao");

        builder.Property(r => r.Id)
            .UseIdentityColumn(1, 1);

        builder.Property(r => r.IniciadoEmUtc)
            .HasColumnType("datetime2(0)");

        builder.HasOne<ArquivoMidia>()
            .WithMany()
            .HasForeignKey(r => r.MidiaId)
            .HasConstraintName("FK_Reproducao_Midia")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PedidoMusica>()
            .WithMany()
            .HasForeignKey(r => r.PedidoId)
            .HasConstraintName("FK_Reproducao_Pedido")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Programa>()
            .WithMany()
            .HasForeignKey(r => r.ProgramaId)
            .HasConstraintName("FK_Reproducao_Programa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => r.IniciadoEmUtc)
            .IncludeProperties(r => r.MidiaId)
            .HasDatabaseName("IX_Reproducao_Periodo");

        builder.HasIndex(r => r.MidiaId)
            .HasDatabaseName("IX_Reproducao_MidiaId");
    }
}
