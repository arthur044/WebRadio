using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using WebRadio.Application.Features.Playout;
using WebRadio.Infrastructure.Playout;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>E1-F12. O caminho com Redis de pé (valor compartilhado entre réplicas, TTL) depende de Redis real: fica para o CI.</summary>
public class TocandoAgoraStoreTests
{
    private static TocandoAgora Faixa(DateTime? quando = null) => new("Faixa", "Banda", null, Guid.NewGuid(), quando ?? DateTime.UtcNow);

    [Fact]
    public async Task Sem_Redis_guarda_na_memoria_e_a_ultima_faixa_vence()
    {
        var store = new TocandoAgoraStore(new RelogioFalso(DateTime.UtcNow), NullLogger<TocandoAgoraStore>.Instance);
        Assert.Null(await store.ObterAsync(default));

        await store.DefinirAsync(Faixa(), default);
        var segunda = Faixa();
        await store.DefinirAsync(segunda, default);

        Assert.Equal(segunda, await store.ObterAsync(default));
    }

    [Fact]
    public async Task Evento_atrasado_nao_desfaz_a_faixa_mais_nova()
    {
        var store = new TocandoAgoraStore(new RelogioFalso(DateTime.UtcNow), NullLogger<TocandoAgoraStore>.Instance);
        var nova = Faixa(DateTime.UtcNow);

        await store.DefinirAsync(nova, default);
        await store.DefinirAsync(Faixa(DateTime.UtcNow.AddMinutes(-3)), default);

        Assert.Equal(nova, await store.ObterAsync(default));
    }

    [Fact]
    public async Task Faixa_mais_velha_que_a_validade_nao_e_mais_o_que_esta_tocando()
    {
        var store = new TocandoAgoraStore(new RelogioFalso(DateTime.UtcNow), NullLogger<TocandoAgoraStore>.Instance);

        await store.DefinirAsync(Faixa(DateTime.UtcNow - TocandoAgoraStore.Validade - TimeSpan.FromMinutes(1)), default);

        Assert.Null(await store.ObterAsync(default));
    }

    [Fact]
    public async Task A_validade_segue_o_relogio_injetado_e_nao_o_do_sistema()
    {
        // Relógio bem longe do real: se o store lesse DateTime.UtcNow, a faixa "de agora" desta data já teria vencido.
        var inicio = new DateTime(2031, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var relogio = new RelogioFalso(inicio);
        var store = new TocandoAgoraStore(relogio, NullLogger<TocandoAgoraStore>.Instance);
        var faixa = Faixa(inicio);

        await store.DefinirAsync(faixa, default);
        Assert.Equal(faixa, await store.ObterAsync(default));

        relogio.Avancar(TocandoAgoraStore.Validade - TimeSpan.FromSeconds(1));
        Assert.Equal(faixa, await store.ObterAsync(default));

        relogio.Avancar(TimeSpan.FromSeconds(2));
        Assert.Null(await store.ObterAsync(default));
    }

    [Fact]
    public async Task Redis_fora_do_ar_nao_derruba_o_evento_do_playout_e_a_leitura_cai_para_a_memoria()
    {
        // Multiplexer real apontando para uma porta fechada, com a mesma configuração tolerante da Api.
        var opcoes = new ConfigurationOptions { AbortOnConnectFail = false, BacklogPolicy = BacklogPolicy.FailFast, ConnectTimeout = 500, AsyncTimeout = 300, SyncTimeout = 300 };
        opcoes.EndPoints.Add("127.0.0.1", 1);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(opcoes);
        var store = new TocandoAgoraStore(new RelogioFalso(DateTime.UtcNow), NullLogger<TocandoAgoraStore>.Instance, redis);
        var faixa = Faixa();

        await store.DefinirAsync(faixa, default);

        Assert.Equal(faixa, await store.ObterAsync(default));
    }
}
