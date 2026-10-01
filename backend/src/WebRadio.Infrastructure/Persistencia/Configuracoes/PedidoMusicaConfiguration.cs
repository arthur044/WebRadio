using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Grade;
using WebRadio.Domain.Interacao;
using WebRadio.Domain.Midia;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Infrastructure.Persistencia.Configuracoes;

public sealed class PedidoMusicaConfiguration : IEntityTypeConfiguration<PedidoMusica>
{
    public void Configure(EntityTypeBuilder<PedidoMusica> builder)
    {
        builder.ToTable("PedidoMusica", "interacao", tb =>
        {
            tb.HasCheckConstraint("CK_PedidoMusica_Status", "Status IN (1, 2, 3, 4, 5)");
            tb.HasCheckConstraint(
                "CK_PedidoMusica_Moderacao",
                "Status NOT IN (2, 3, 4) OR (ModeradoPorUsuarioId IS NOT NULL AND ModeradoEmUtc IS NOT NULL)");
            tb.HasCheckConstraint("CK_PedidoMusica_Rejeicao", "Status <> 3 OR MotivoRejeicao IS NOT NULL");
            tb.HasCheckConstraint("CK_PedidoMusica_Tocado", "Status <> 4 OR TocadoEmUtc IS NOT NULL");
        });

        builder.HasKey(p => p.Id).HasName("PK_PedidoMusica");

        builder.Property(p => p.Id)
            .HasDefaultValueSql("NEWSEQUENTIALID()")
            .HasDefaultConstraintName("DF_PedidoMusica_Id")
            .ValueGeneratedOnAdd();

        builder.Property(p => p.NomeOuvinte).HasMaxLength(60).IsRequired();
        builder.Property(p => p.TituloMusica).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Artista).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Mensagem).HasMaxLength(280);
        builder.Property(p => p.MotivoRejeicao).HasMaxLength(280);

        builder.Property(p => p.ListenerDeviceHash).HasColumnType("binary(32)");
        builder.Property(p => p.ListenerIpHash).HasColumnType("binary(32)");

        builder.Property(p => p.Status)
            .HasColumnType("tinyint")
            .HasDefaultValue(StatusPedido.Pendente)
            .HasDefaultConstraintName("DF_PedidoMusica_Status");

        builder.Property(p => p.ModeradoEmUtc).HasColumnType("datetime2(3)");
        builder.Property(p => p.EnviadoPlayoutEmUtc).HasColumnType("datetime2(3)");
        builder.Property(p => p.TocadoEmUtc).HasColumnType("datetime2(3)");
        builder.Property(p => p.CriadoEmUtc)
            .HasColumnType("datetime2(3)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .HasDefaultConstraintName("DF_PedidoMusica_CriadoEmUtc");

        builder.Property(p => p.RowVersion).IsRowVersion();

        builder.HasOne<Programa>()
            .WithMany()
            .HasForeignKey(p => p.ProgramaId)
            .HasConstraintName("FK_PedidoMusica_Programa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(p => p.UsuarioId)
            .HasConstraintName("FK_PedidoMusica_Usuario")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ArquivoMidia>()
            .WithMany()
            .HasForeignKey(p => p.MidiaId)
            .HasConstraintName("FK_PedidoMusica_Midia")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(p => p.ModeradoPorUsuarioId)
            .HasConstraintName("FK_PedidoMusica_ModeradoPor")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.CriadoEmUtc)
            .IncludeProperties(p => new { p.NomeOuvinte, p.TituloMusica, p.Artista, p.Mensagem, p.ProgramaId })
            .HasFilter("Status = 1")
            .HasDatabaseName("IX_PedidoMusica_Fila");

        builder.HasIndex(p => p.ModeradoEmUtc)
            .IncludeProperties(p => p.MidiaId)
            .HasFilter("Status = 2 AND MidiaId IS NOT NULL AND EnviadoPlayoutEmUtc IS NULL")
            .HasDatabaseName("IX_PedidoMusica_Playout");

        builder.HasIndex(p => new { p.ListenerDeviceHash, p.CriadoEmUtc })
            .IsDescending(false, true)
            .HasFilter("ListenerDeviceHash IS NOT NULL")
            .HasDatabaseName("IX_PedidoMusica_Device");

        builder.HasIndex(p => new { p.ListenerIpHash, p.CriadoEmUtc })
            .IsDescending(false, true)
            .HasFilter("ListenerIpHash IS NOT NULL")
            .HasDatabaseName("IX_PedidoMusica_Ip");

        builder.HasIndex(p => new { p.UsuarioId, p.CriadoEmUtc })
            .IsDescending(false, true)
            .HasFilter("UsuarioId IS NOT NULL")
            .HasDatabaseName("IX_PedidoMusica_Usuario");

        builder.HasIndex(p => p.ProgramaId)
            .HasFilter("ProgramaId IS NOT NULL")
            .HasDatabaseName("IX_PedidoMusica_ProgramaId");

        builder.HasIndex(p => p.MidiaId)
            .HasFilter("MidiaId IS NOT NULL")
            .HasDatabaseName("IX_PedidoMusica_MidiaId");

        builder.IgnorarEventosDominio();
    }
}
