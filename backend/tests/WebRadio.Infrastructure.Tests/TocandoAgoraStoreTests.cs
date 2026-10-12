using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using WebRadio.Application.Features.Playout;
using WebRadio.Infrastructure.Playout;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>E1-F12. O caminho com Redis de pé (valor compartilhado entre réplicas, TTL) depende de Redis real: fica para o CI.</summary>
public class TocandoAgoraStoreTests
{
    private static TocandoAgora Faixa(DateTime? quando = null) => new("Faixa", "Banda", null, Guid.NewGuid(), quando ?? DateTime.UtcNow);

    [Fact]
    public async Task Sem_Redis_guarda_na_memoria_e_a_ultima_faixa_vence()
    {
        var store = new TocandoAgoraStore(NullLogger<TocandoAgoraStore>.Instance);
        Assert.Null(await store.ObterAsync(default));

        await store.DefinirAsync(Faixa(), default);
        var segunda = Faixa();
        await store.DefinirAsync(segunda, default);

        Assert.Equal(segunda, await store.ObterAsync(default));
    }

    [Fact]
    public async Task Faixa_mais_velha_que_a_validade_nao_e_mais_o_que_esta_tocando()
    {
        var store = new TocandoAgoraStore(NullLogger<TocandoAgoraStore>.Instance);

        await store.DefinirAsync(Faixa(DateTime.UtcNow - TocandoAgoraStore.Validade - TimeSpan.FromMinutes(1)), default);

        Assert.Null(await store.ObterAsync(default));
    }

    [Fact]
    public async Task Redis_fora_do_ar_nao_derruba_o_evento_do_playout_e_a_leitura_cai_para_a_memoria()
    {
        // Multiplexer real apontando para uma porta fechada, com a mesma configuração tolerante da Api.
        var opcoes = new ConfigurationOptions { AbortOnConnectFail = false, BacklogPolicy = BacklogPolicy.FailFast, ConnectTimeout = 500, AsyncTimeout = 300, SyncTimeout = 300 };
        opcoes.EndPoints.Add("127.0.0.1", 1);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(opcoes);
        var store = new TocandoAgoraStore(NullLogger<TocandoAgoraStore>.Instance, redis);
        var faixa = Faixa();

        await store.DefinirAsync(faixa, default);

        Assert.Equal(faixa, await store.ObterAsync(default));
    }
}
