using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Midia;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Infrastructure.Persistencia.Configuracoes;

public sealed class ArquivoMidiaConfiguration : IEntityTypeConfiguration<ArquivoMidia>
{
    public void Configure(EntityTypeBuilder<ArquivoMidia> builder)
    {
        builder.ToTable("ArquivoMidia", "midia", tb =>
        {
            tb.HasCheckConstraint("CK_ArquivoMidia_Tipo", "TipoMidia IN (1, 2, 3)");
            tb.HasCheckConstraint("CK_ArquivoMidia_Status", "StatusSanitizacao IN (1, 2, 3, 4, 5, 6)");
            tb.HasCheckConstraint("CK_ArquivoMidia_Bucket", "Bucket IN ('quarentena', 'midia')");
            tb.HasCheckConstraint("CK_ArquivoMidia_Tamanho", "TamanhoBytes > 0 AND TamanhoBytes <= 262144000");
            tb.HasCheckConstraint("CK_ArquivoMidia_Duracao", "DuracaoSegundos IS NULL OR DuracaoSegundos > 0");
            tb.HasCheckConstraint(
                "CK_ArquivoMidia_MimeType",
                "MimeType IS NULL OR MimeType IN ('audio/mpeg', 'audio/ogg', 'audio/wav', 'audio/flac')");
            tb.HasCheckConstraint("CK_ArquivoMidia_TentativasSanitizacao", "TentativasSanitizacao BETWEEN 0 AND 10");
            tb.HasCheckConstraint("CK_ArquivoMidia_EmAnalise", "StatusSanitizacao <> 3 OR EmAnaliseDesdeUtc IS NOT NULL");
            tb.HasCheckConstraint(
                "CK_ArquivoMidia_AprovadoCompleto",
                "StatusSanitizacao <> 4 OR (MimeType IS NOT NULL AND HashSHA256 IS NOT NULL AND DuracaoSegundos IS NOT NULL AND SanitizadoEmUtc IS NOT NULL AND Bucket = 'midia')");
            tb.HasCheckConstraint("CK_ArquivoMidia_Musica_Titulo", "TipoMidia <> 3 OR Titulo IS NOT NULL");
        });

        builder.HasKey(m => m.Id).HasName("PK_ArquivoMidia");

        builder.Property(m => m.Id)
            .HasDefaultValueSql("NEWSEQUENTIALID()")
            .HasDefaultConstraintName("DF_ArquivoMidia_Id")
            .ValueGeneratedOnAdd();

        builder.Property(m => m.NomeOriginal).HasMaxLength(255).IsRequired();
        builder.Property(m => m.Titulo).HasMaxLength(200);
        builder.Property(m => m.Artista).HasMaxLength(200);
        builder.Property(m => m.TipoMidia).HasColumnType("tinyint");

        builder.Property(m => m.Bucket)
            .HasColumnType("varchar(63)")
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired();

        builder.Property(m => m.ChaveStorage)
            .HasColumnType("varchar(512)")
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired();

        builder.Property(m => m.MimeTypeDeclarado)
            .HasColumnType("varchar(100)")
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired();

        builder.Property(m => m.MimeType)
            .HasColumnType("varchar(100)")
            .UseCollation("Latin1_General_100_BIN2");

        builder.Property(m => m.EtagUpload)
            .HasColumnType("varchar(64)")
            .UseCollation("Latin1_General_100_BIN2");

        builder.Property(m => m.TamanhoBytes);

        builder.Property(m => m.HashOriginalSHA256).HasColumnType("binary(32)");
        builder.Property(m => m.HashSHA256).HasColumnType("binary(32)");
        builder.Property(m => m.DuracaoSegundos).HasColumnType("decimal(9,3)");

        builder.Property(m => m.StatusSanitizacao)
            .HasColumnType("tinyint")
            .HasDefaultValue(StatusSanitizacao.AguardandoUpload)
            .HasDefaultConstraintName("DF_ArquivoMidia_Status");

        builder.Property(m => m.TentativasSanitizacao)
            .HasDefaultValue((byte)0)
            .HasDefaultConstraintName("DF_ArquivoMidia_TentativasSanitizacao");

        builder.Property(m => m.EmAnaliseDesdeUtc).HasColumnType("datetime2(3)");
        builder.Property(m => m.MotivoRejeicao).HasMaxLength(500);

        builder.Property(m => m.DataUploadUtc)
            .HasColumnType("datetime2(3)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .HasDefaultConstraintName("DF_ArquivoMidia_DataUploadUtc");

        builder.Property(m => m.UploadExpiraEmUtc).HasColumnType("datetime2(3)");
        builder.Property(m => m.SanitizadoEmUtc).HasColumnType("datetime2(3)");

        builder.Property(m => m.Ativo)
            .HasDefaultValue(true)
            .HasDefaultConstraintName("DF_ArquivoMidia_Ativo");
        builder.Property(m => m.RowVersion).IsRowVersion();

        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(m => m.EnviadoPorUsuarioId)
            .HasConstraintName("FK_ArquivoMidia_EnviadoPor")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => new { m.Bucket, m.ChaveStorage })
            .IsUnique()
            .HasDatabaseName("UX_ArquivoMidia_Objeto");

        builder.HasIndex(m => m.HashSHA256)
            .IsUnique()
            .HasFilter("StatusSanitizacao = 4 AND HashSHA256 IS NOT NULL")
            .HasDatabaseName("UX_ArquivoMidia_Hash_Aprovado");

        builder.HasIndex(m => m.HashOriginalSHA256)
            .HasFilter("HashOriginalSHA256 IS NOT NULL")
            .HasDatabaseName("IX_ArquivoMidia_HashOriginal");

        builder.HasIndex(m => new { m.TipoMidia, m.StatusSanitizacao })
            .IncludeProperties(m => new { m.Titulo, m.Artista, m.DuracaoSegundos, m.Ativo })
            .HasDatabaseName("IX_ArquivoMidia_Biblioteca");

        builder.HasIndex(m => new { m.StatusSanitizacao, m.DataUploadUtc })
            .IncludeProperties(m => new { m.UploadExpiraEmUtc, m.TentativasSanitizacao, m.EmAnaliseDesdeUtc })
            .HasFilter("StatusSanitizacao IN (1, 2, 3)")
            .HasDatabaseName("IX_ArquivoMidia_FilaSanitizacao");

        builder.HasIndex(m => m.EnviadoPorUsuarioId)
            .HasDatabaseName("IX_ArquivoMidia_EnviadoPor");

        builder.IgnorarEventosDominio();
    }
}
