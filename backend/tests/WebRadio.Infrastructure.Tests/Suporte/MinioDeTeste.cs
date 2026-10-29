using Xunit;

namespace WebRadio.Infrastructure.Tests.Suporte;

/// <summary>
/// MinIO real para os testes do storage (o comportamento do <c>x-amz-copy-source-if-match</c> e do 412 só existe no servidor).
/// Variáveis: WEBRADIO_TEST_MINIO (host:porta), WEBRADIO_TEST_MINIO_ACCESS e WEBRADIO_TEST_MINIO_SECRET (credencial root do
/// contêiner de teste). Sem elas, os [MinioFact] aparecem como ignorados com o motivo (o job do CI barra testes ignorados).
/// </summary>
public static class MinioDeTeste
{
    public static string? Endpoint { get; } = Ler("WEBRADIO_TEST_MINIO");

    public static string? AccessKey { get; } = Ler("WEBRADIO_TEST_MINIO_ACCESS");

    public static string? SecretKey { get; } = Ler("WEBRADIO_TEST_MINIO_SECRET");

    public static bool Disponivel => Endpoint is not null && AccessKey is not null && SecretKey is not null;

    private static string? Ler(string nome)
    {
        var valor = Environment.GetEnvironmentVariable(nome);
        return string.IsNullOrWhiteSpace(valor) ? null : valor;
    }
}

/// <summary>[Fact] que só roda com MinIO disponível; senão aparece como ignorado, nunca como verde falso.</summary>
public sealed class MinioFactAttribute : FactAttribute
{
    public MinioFactAttribute()
    {
        if (!MinioDeTeste.Disponivel)
        {
            Skip = "MinIO indisponível (defina WEBRADIO_TEST_MINIO=host:porta, WEBRADIO_TEST_MINIO_ACCESS e WEBRADIO_TEST_MINIO_SECRET).";
        }
    }
}

/// <summary>[Fact] que precisa de SQL Server E de MinIO reais (a limpeza das imagens órfãs cruza o banco e o bucket); senão aparece como ignorado.</summary>
public sealed class SqlEMinioFactAttribute : FactAttribute
{
    public SqlEMinioFactAttribute()
    {
        if (!BancoDeTeste.Disponivel) Skip = "SQL Server indisponível (defina WEBRADIO_TEST_SQL ou instale o LocalDB).";
        else if (!MinioDeTeste.Disponivel) Skip = "MinIO indisponível (defina WEBRADIO_TEST_MINIO, WEBRADIO_TEST_MINIO_ACCESS e WEBRADIO_TEST_MINIO_SECRET).";
    }
}
