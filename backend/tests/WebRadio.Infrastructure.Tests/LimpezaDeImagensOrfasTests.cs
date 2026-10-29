using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Divulgacoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Interacao;
using WebRadio.Infrastructure.Armazenamento;
using WebRadio.Infrastructure.Tests.Suporte;
using WebRadio.Infrastructure.Trabalhos;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// E3-F07 / E3-D6 contra SQL Server e MinIO de verdade: a <c>Manutencao</c> apaga as imagens de <c>publico/divulgacoes/</c> com mais de 24 h que nenhuma
/// divulgação usa, e só elas. A idade vem do <c>LastModified</c> do MinIO; o relógio da manutenção anda 25 h para o objeto "ficar velho".
/// </summary>
public class LimpezaDeImagensOrfasTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private readonly Cenario _c = new(banco);

    private static IMinioClient Admin() => new MinioClient().WithEndpoint(MinioDeTeste.Endpoint).WithCredentials(MinioDeTeste.AccessKey, MinioDeTeste.SecretKey).WithSSL(false).Build();

    private static MinioObjectStorage Storage(RelogioFalso relogio) =>
        new(new StorageOptions(MinioDeTeste.Endpoint!, "http://" + MinioDeTeste.Endpoint + "/storage", MinioDeTeste.AccessKey!, MinioDeTeste.SecretKey!), relogio);

    private static async Task GarantirBucketAsync(IMinioClient admin)
    {
        if (!await admin.BucketExistsAsync(new BucketExistsArgs().WithBucket(Buckets.Publico)))
            await admin.MakeBucketAsync(new MakeBucketArgs().WithBucket(Buckets.Publico));
    }

    private static Task PutAsync(IMinioClient admin, string chave) =>
        admin.PutObjectAsync(new PutObjectArgs().WithBucket(Buckets.Publico).WithObject(chave).WithStreamData(new MemoryStream([1, 2, 3])).WithObjectSize(3).WithContentType("image/webp"));

    private static async Task<bool> ExisteAsync(IMinioClient admin, string chave)
    {
        try
        {
            await admin.StatObjectAsync(new StatObjectArgs().WithBucket(Buckets.Publico).WithObject(chave));
            return true;
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            return false;
        }
    }

    private async Task<ResultadoDaManutencao> ManterAsync(RelogioFalso relogio)
    {
        await using var db = _c.NovoDb();
        using var storage = Storage(relogio);
        return await new Manutencao(db, storage, relogio).ExecutarAsync(default);
    }

    [SqlEMinioFact]
    public async Task Orfa_velha_some_e_orfa_nova_imagem_ligada_e_chave_fora_do_formato_ficam()
    {
        using var admin = Admin();
        await GarantirBucketAsync(admin);
        var orfa = ChaveDeImagem.Nova();
        var ligada = ChaveDeImagem.Nova();
        var estranha = "divulgacoes/nao-e-uma-chave-do-servidor.webp";
        foreach (var chave in new[] { orfa, ligada, estranha }) await PutAsync(admin, chave);

        var dono = await _c.CriarUsuarioAsync(Cenario.Email(), Role.Admin);
        await using (var db = _c.NovoDb())
        {
            db.Divulgacoes.Add(new Divulgacao(Guid.NewGuid(), "Ligada", "M", ligada, null, 1, null, null, dono.Id, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        try
        {
            // Agora: nada tem 24 h, nada sai (a órfã recém-enviada espera o Admin salvar o formulário).
            Assert.Equal(0, (await ManterAsync(new RelogioFalso(DateTime.UtcNow))).ImagensApagadas);
            Assert.True(await ExisteAsync(admin, orfa));

            // Daqui a 25 h: a órfã sai; a ligada a uma divulgação e a de nome fora do formato do E3-D5 ficam.
            var r = await ManterAsync(new RelogioFalso(DateTime.UtcNow.AddHours(25)));

            Assert.True(r.ImagensApagadas >= 1);
            Assert.False(await ExisteAsync(admin, orfa));
            Assert.True(await ExisteAsync(admin, ligada));
            Assert.True(await ExisteAsync(admin, estranha));
            Assert.Equal(0, (await ManterAsync(new RelogioFalso(DateTime.UtcNow.AddHours(25)))).ImagensApagadas); // idempotente
        }
        finally
        {
            foreach (var chave in new[] { orfa, ligada, estranha })
                await admin.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Buckets.Publico).WithObject(chave));
            await using var db = _c.NovoDb();
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM interacao.Divulgacao WHERE ImagemChaveStorage = {ligada}");
        }
    }

    [SqlEMinioFact]
    public async Task Divulgacao_criada_depois_da_listagem_nao_perde_a_imagem_a_referencia_e_reconferida_antes_de_apagar()
    {
        using var admin = Admin();
        await GarantirBucketAsync(admin);
        var chave = ChaveDeImagem.Nova();
        await PutAsync(admin, chave);
        var dono = await _c.CriarUsuarioAsync(Cenario.Email(), Role.Admin);
        var relogio = new RelogioFalso(DateTime.UtcNow.AddHours(25));
        await using var db = _c.NovoDb();
        // A divulgação nasce DEPOIS de o storage ser consultado mas ANTES do DeleteObject: o storage de mentira cria a linha na listagem.
        using var real = Storage(relogio);
        var intermediario = new StorageQueCriaDivulgacaoNaListagem(real, _c, chave, dono.Id);
        try
        {
            var r = await new Manutencao(db, intermediario, relogio).ExecutarAsync(default);

            Assert.True(await ExisteAsync(admin, chave));
            Assert.True(r.ImagensApagadas >= 0);
        }
        finally
        {
            await admin.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Buckets.Publico).WithObject(chave));
            await using var limpa = _c.NovoDb();
            await limpa.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM interacao.Divulgacao WHERE ImagemChaveStorage = {chave}");
        }
    }

    [SqlEMinioFact]
    public async Task Falha_do_storage_na_limpeza_nao_derruba_a_manutencao_do_banco()
    {
        await using var db = _c.NovoDb();
        var storageQuebrado = new StorageQueFalha();

        var r = await new Manutencao(db, storageQuebrado, new RelogioFalso(DateTime.UtcNow)).ExecutarAsync(default);

        Assert.Equal(0, r.ImagensApagadas);
    }

    private sealed class StorageQueFalha : IObjectStorage
    {
        public Task<IReadOnlyList<ObjetoListado>> ListarAsync(string bucket, string prefixo, CancellationToken ct = default) => throw new InvalidOperationException("MinIO fora");

        public Task DeleteAsync(string bucket, string chave, CancellationToken ct = default) => throw new NotSupportedException();

        public Task PutAsync(string bucket, string chave, Stream conteudo, long tamanhoBytes, string contentType, CancellationToken ct = default) => throw new NotSupportedException();

        public string UrlPublica(string bucket, string chave) => throw new NotSupportedException();

        public Task<PostPolicyAssinada> CriarPostPolicyAsync(string bucket, string chave, string contentType, long tamanhoMaximoBytes, TimeSpan validade, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string> UrlGetPreAssinadaAsync(string bucket, string chave, TimeSpan validade, string? contentType = null, string? contentDisposition = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<InfoObjeto?> HeadAsync(string bucket, string chave, CancellationToken ct = default) => throw new NotSupportedException();

        public Task MoverAsync(string bucketOrigem, string chaveOrigem, string bucketDestino, string chaveDestino, string etagEsperado, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> VerificarAsync(CancellationToken ct = default) => Task.FromResult(false);
    }

    /// <summary>Repassa tudo ao storage real; depois de listar, insere a divulgação que passa a usar a imagem (a corrida do E3-D6).</summary>
    private sealed class StorageQueCriaDivulgacaoNaListagem(MinioObjectStorage real, Cenario c, string chave, Guid dono) : IObjectStorage
    {
        public async Task<IReadOnlyList<ObjetoListado>> ListarAsync(string bucket, string prefixo, CancellationToken ct = default)
        {
            var lista = await real.ListarAsync(bucket, prefixo, ct);
            await using var db = c.NovoDb();
            db.Divulgacoes.Add(new Divulgacao(Guid.NewGuid(), "Criada no meio", "M", chave, null, 1, null, null, dono, DateTime.UtcNow));
            await db.SaveChangesAsync(ct);
            return lista;
        }

        public Task DeleteAsync(string bucket, string chave, CancellationToken ct = default) => real.DeleteAsync(bucket, chave, ct);

        public Task PutAsync(string bucket, string chave, Stream conteudo, long tamanhoBytes, string contentType, CancellationToken ct = default) => real.PutAsync(bucket, chave, conteudo, tamanhoBytes, contentType, ct);

        public string UrlPublica(string bucket, string chave) => real.UrlPublica(bucket, chave);

        public Task<PostPolicyAssinada> CriarPostPolicyAsync(string bucket, string chave, string contentType, long tamanhoMaximoBytes, TimeSpan validade, CancellationToken ct = default) => real.CriarPostPolicyAsync(bucket, chave, contentType, tamanhoMaximoBytes, validade, ct);

        public Task<string> UrlGetPreAssinadaAsync(string bucket, string chave, TimeSpan validade, string? contentType = null, string? contentDisposition = null, CancellationToken ct = default) => real.UrlGetPreAssinadaAsync(bucket, chave, validade, contentType, contentDisposition, ct);

        public Task<InfoObjeto?> HeadAsync(string bucket, string chave, CancellationToken ct = default) => real.HeadAsync(bucket, chave, ct);

        public Task MoverAsync(string bucketOrigem, string chaveOrigem, string bucketDestino, string chaveDestino, string etagEsperado, CancellationToken ct = default) => real.MoverAsync(bucketOrigem, chaveOrigem, bucketDestino, chaveDestino, etagEsperado, ct);

        public Task<bool> VerificarAsync(CancellationToken ct = default) => real.VerificarAsync(ct);
    }
}
