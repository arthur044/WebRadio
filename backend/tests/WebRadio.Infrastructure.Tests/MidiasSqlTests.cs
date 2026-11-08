using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Midias;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Seguranca;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// E4-F02 contra SQL Server de verdade (DoD 7): o limite atômico de 5 abertos e o de 20 por hora, os dois com a trava por usuário
/// (<c>sp_getapplock</c>) e a contagem na mesma transação do insert.
/// </summary>
public class MidiasSqlTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private readonly Cenario _c = new(banco);

    private sealed class StoragePolicy : IObjectStorage
    {
        public Task<PostPolicyAssinada> CriarPostPolicyAsync(string bucket, string chave, string contentType, long tamanhoMaximoBytes, TimeSpan validade, CancellationToken ct = default) =>
            Task.FromResult(new PostPolicyAssinada("http://localhost/storage/quarentena", new Dictionary<string, string> { ["key"] = chave }, DateTime.UtcNow + validade));

        public Task<InfoObjeto?> HeadAsync(string bucket, string chave, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string> UrlGetPreAssinadaAsync(string bucket, string chave, TimeSpan validade, string? contentType = null, string? contentDisposition = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task MoverAsync(string bucketOrigem, string chaveOrigem, string bucketDestino, string chaveDestino, string etagEsperado, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> VerificarAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task PutAsync(string bucket, string chave, Stream conteudo, long tamanhoBytes, string contentType, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(string bucket, string chave, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ObjetoListado>> ListarAsync(string bucket, string prefixo, CancellationToken ct = default) => throw new NotSupportedException();

        public string UrlPublica(string bucket, string chave) => throw new NotSupportedException();
    }

    /// <summary>Um handler por chamada (contexto próprio, como numa requisição); o limite por hora é o mesmo objeto, como o singleton da Api.</summary>
    private Func<IniciarUploadCommand, Task<bool>> Tentar(MidiaOptions opcoes, IRequestThrottle? redis = null)
    {
        var hasher = new IpHasher(_c.Pepper);
        var limite = new LimiteDeUploadDeMidia(redis ?? new MemoryRequestThrottle(_c.Relogio), hasher, _c.Relogio, Options.Create(opcoes), NullLogger<LimiteDeUploadDeMidia>.Instance);
        return async cmd =>
        {
            await using var db = _c.NovoDb();
            var handler = new IniciarUploadHandler(db, new TransacaoComTrava(db), _c.Relogio, hasher, limite, Options.Create(opcoes), new StoragePolicy());
            try
            {
                await handler.Handle(cmd, default);
                return true;
            }
            catch (LimiteExcedidoException)
            {
                return false;
            }
        };
    }

    private static IniciarUploadCommand Comando(Guid usuario) => new(usuario, "vinheta.mp3", null, null, TipoMidia.Vinheta, "audio/mpeg", 1000);

    [SqlFact]
    public async Task DoD_7_vinte_POST_paralelos_do_mesmo_usuario_sem_abertos_dao_exatamente_5_criados_e_15_recusados()
    {
        var usuario = (await _c.CriarUsuarioAsync(Cenario.Email(), Role.Locutor)).Id;
        var tentar = Tentar(new MidiaOptions());

        var resultados = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => tentar(Comando(usuario)))));

        Assert.Equal(5, resultados.Count(r => r));
        Assert.Equal(15, resultados.Count(r => !r));
        await using var db = _c.NovoDb();
        Assert.Equal(5, await db.ArquivosMidia.CountAsync(m => m.EnviadoPorUsuarioId == usuario));
    }

    [SqlFact]
    public async Task Os_abertos_de_um_usuario_nao_travam_nem_contam_para_o_outro()
    {
        var a = (await _c.CriarUsuarioAsync(Cenario.Email(), Role.Locutor)).Id;
        var b = (await _c.CriarUsuarioAsync(Cenario.Email(), Role.Locutor)).Id;
        var tentar = Tentar(new MidiaOptions());

        var resultados = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => tentar(Comando(i % 2 == 0 ? a : b)))));

        Assert.Equal(10, resultados.Count(r => r));
        await using var db = _c.NovoDb();
        Assert.Equal(5, await db.ArquivosMidia.CountAsync(m => m.EnviadoPorUsuarioId == a));
        Assert.Equal(5, await db.ArquivosMidia.CountAsync(m => m.EnviadoPorUsuarioId == b));
    }

    [SqlFact]
    public async Task Com_o_redis_fora_o_limite_de_20_por_hora_tambem_vale_sob_concorrencia_no_sql()
    {
        var usuario = (await _c.CriarUsuarioAsync(Cenario.Email(), Role.Locutor)).Id;
        var tentar = Tentar(new MidiaOptions { MaximoAbertos = 100 }, new RedisSempreFora());

        var resultados = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() => tentar(Comando(usuario)))));

        Assert.Equal(20, resultados.Count(r => r));
        await using var db = _c.NovoDb();
        Assert.Equal(20, await db.ArquivosMidia.CountAsync(m => m.EnviadoPorUsuarioId == usuario));
    }

    private sealed class RedisSempreFora : IRequestThrottle
    {
        private static Exception Fora() => new InvalidOperationException("Redis fora");

        public Task<bool> TentarReservarAsync(string chave, TimeSpan ttl, CancellationToken ct = default) => throw Fora();

        public Task<long> IncrementarAsync(string chave, TimeSpan ttl, CancellationToken ct = default) => throw Fora();

        public Task<long> LerAsync(string chave, CancellationToken ct = default) => throw Fora();

        public Task RemoverAsync(string chave, CancellationToken ct = default) => throw Fora();

        public Task<TimeSpan?> TempoRestanteAsync(string chave, CancellationToken ct = default) => throw Fora();
    }
}
