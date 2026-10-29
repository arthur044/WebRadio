using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Armazenamento;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// S-M02 contra MinIO de verdade. O SDK descarta o <c>WithMatchETag</c> e engole o 412 da cópia condicional (Minio 6.0.4 e
/// 7.0.0): sem estes testes, um <c>MoverAsync</c> que "confere o ETag" só no código passava verde e movia o objeto trocado.
/// </summary>
public sealed class MinioObjectStorageRealTests : IAsyncLifetime
{
    private readonly string _origem = "origem-" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _destino = "destino-" + Guid.NewGuid().ToString("N")[..12];
    private IMinioClient? _admin;
    private MinioObjectStorage? _storage;

    public async Task InitializeAsync()
    {
        if (!MinioDeTeste.Disponivel) return;

        _admin = new MinioClient().WithEndpoint(MinioDeTeste.Endpoint).WithCredentials(MinioDeTeste.AccessKey, MinioDeTeste.SecretKey)
            .WithSSL(false).WithRegion("us-east-1").Build();
        foreach (var bucket in new[] { _origem, _destino })
        {
            await _admin.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));
        }

        _storage = new MinioObjectStorage(
            new StorageOptions(MinioDeTeste.Endpoint!, "http://" + MinioDeTeste.Endpoint + "/", MinioDeTeste.AccessKey!, MinioDeTeste.SecretKey!),
            new RelogioFalso(DateTime.UtcNow));
    }

    public async Task DisposeAsync()
    {
        _storage?.Dispose();
        if (_admin is null) return;

        foreach (var bucket in new[] { _origem, _destino })
        {
            await foreach (var item in _admin.ListObjectsEnumAsync(new ListObjectsArgs().WithBucket(bucket).WithRecursive(true)))
            {
                await _admin.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject(item.Key));
            }

            await _admin.RemoveBucketAsync(new RemoveBucketArgs().WithBucket(bucket));
        }

        _admin.Dispose();
    }

    private async Task EnviarAsync(string chave, byte conteudo, int tamanho = 100) =>
        await _admin!.PutObjectAsync(new PutObjectArgs().WithBucket(_origem).WithObject(chave)
            .WithStreamData(new MemoryStream(Enumerable.Repeat(conteudo, tamanho).ToArray())).WithObjectSize(tamanho).WithContentType("audio/mpeg"));

    [MinioFact]
    public async Task Put_grava_com_o_Content_Type_dado_e_Delete_apaga_e_apagar_o_que_nao_existe_nao_e_erro()
    {
        var bytes = Enumerable.Repeat((byte)7, 321).ToArray();

        await _storage!.PutAsync(_origem, "divulgacoes/a.webp", new MemoryStream(bytes), bytes.Length, "image/webp");

        var info = (await _storage.HeadAsync(_origem, "divulgacoes/a.webp"))!;
        Assert.Equal(321, info.TamanhoBytes);
        Assert.Equal("image/webp", info.ContentType);

        await _storage.DeleteAsync(_origem, "divulgacoes/a.webp");
        Assert.Null(await _storage.HeadAsync(_origem, "divulgacoes/a.webp"));
        await _storage.DeleteAsync(_origem, "divulgacoes/a.webp"); // de novo: sem erro
    }

    [MinioFact]
    public async Task Listar_traz_so_o_prefixo_com_a_data_de_modificacao_em_UTC()
    {
        foreach (var chave in new[] { "divulgacoes/a.webp", "divulgacoes/b.webp", "midia/x.mp3" })
            await _storage!.PutAsync(_origem, chave, new MemoryStream([1, 2, 3]), 3, "application/octet-stream");

        var lista = await _storage!.ListarAsync(_origem, "divulgacoes/");

        Assert.Equal(["divulgacoes/a.webp", "divulgacoes/b.webp"], lista.Select(o => o.Chave).Order());
        Assert.All(lista, o =>
        {
            Assert.Equal(DateTimeKind.Utc, o.UltimaModificacaoUtc.Kind);
            Assert.InRange(o.UltimaModificacaoUtc, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
        });
        Assert.Empty(await _storage.ListarAsync(_origem, "nada/"));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.ListarAsync(_origem, " "));
    }

    [Fact]
    public void UrlPublica_junta_a_origem_do_storage_o_bucket_e_a_chave_sem_assinatura()
    {
        using var storage = new MinioObjectStorage(
            new StorageOptions("minio:9000", "http://localhost:8080/storage", "a", "b"), new RelogioFalso(DateTime.UtcNow));

        Assert.Equal("http://localhost:8080/storage/publico/divulgacoes/abc.webp", storage.UrlPublica("publico", "divulgacoes/abc.webp"));
    }

    [MinioFact]
    public async Task Mover_com_o_etag_certo_copia_confere_o_destino_e_apaga_a_origem()
    {
        await EnviarAsync("faixa", 1);
        var etag = (await _storage!.HeadAsync(_origem, "faixa"))!.ETag!;

        await _storage.MoverAsync(_origem, "faixa", _destino, "faixa-final", etag);

        Assert.Null(await _storage.HeadAsync(_origem, "faixa"));
        Assert.Equal(etag, (await _storage.HeadAsync(_destino, "faixa-final"))!.ETag);
    }

    [MinioFact]
    public async Task Mover_com_o_etag_entre_aspas_tambem_funciona()
    {
        await EnviarAsync("faixa", 1);
        var etag = (await _storage!.HeadAsync(_origem, "faixa"))!.ETag!;

        await _storage.MoverAsync(_origem, "faixa", _destino, "faixa-final", "\"" + etag + "\"");

        Assert.Null(await _storage.HeadAsync(_origem, "faixa"));
        Assert.NotNull(await _storage.HeadAsync(_destino, "faixa-final"));
    }

    [MinioFact]
    public async Task Mover_com_etag_errado_nao_copia_e_nao_apaga_a_origem()
    {
        await EnviarAsync("faixa", 1);

        var e = await Assert.ThrowsAsync<EtagDivergenteException>(
            () => _storage!.MoverAsync(_origem, "faixa", _destino, "faixa-final", new string('0', 32)));

        // Só o servidor recusando a cópia (header enviado) produz o 412; a limpeza do destino, que também deixa o bucket vazio, não.
        Assert.IsType<PreconditionFailedException>(e.InnerException);

        Assert.NotNull(await _storage!.HeadAsync(_origem, "faixa"));
        Assert.Null(await _storage.HeadAsync(_destino, "faixa-final"));
    }

    [MinioFact]
    public async Task Objeto_trocado_depois_da_verificacao_nao_chega_ao_destino_TOCTOU()
    {
        await EnviarAsync("faixa", 1);
        var verificado = (await _storage!.HeadAsync(_origem, "faixa"))!.ETag!;

        // A POST policy aceita outro POST na mesma key depois do concluir: o conteúdo muda e o ETag verificado não vale mais.
        await EnviarAsync("faixa", 2, tamanho: 200);

        var e = await Assert.ThrowsAsync<EtagDivergenteException>(
            () => _storage.MoverAsync(_origem, "faixa", _destino, "faixa-final", verificado));
        Assert.IsType<PreconditionFailedException>(e.InnerException);

        Assert.Null(await _storage.HeadAsync(_destino, "faixa-final"));
        var restante = await _storage.HeadAsync(_origem, "faixa");
        Assert.Equal(200, restante!.TamanhoBytes);
    }
}
