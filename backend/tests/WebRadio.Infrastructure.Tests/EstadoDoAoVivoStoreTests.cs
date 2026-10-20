using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Application.Features.Harbor;
using WebRadio.Infrastructure.Harbor;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>E2-F06. O caminho com Redis de pé (valor compartilhado entre réplicas) depende de Redis real, como o do "tocando agora".</summary>
public class EstadoDoAoVivoStoreTests
{
    private static readonly DateTime Inicio = new(2031, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static EstadoDoAoVivo NoAr(DateTime desde, DateTime fim) => new(true, Guid.NewGuid(), "Ana", Guid.NewGuid(), desde, fim);

    [Fact]
    public async Task Sem_Redis_guarda_na_memoria()
    {
        var store = new EstadoDoAoVivoStore(new RelogioFalso(Inicio), NullLogger<EstadoDoAoVivoStore>.Instance);
        Assert.Null(await store.ObterAsync(default));

        var estado = NoAr(Inicio, Inicio.AddHours(1));
        await store.DefinirAsync(estado, default);

        Assert.Equal(estado, await store.ObterAsync(default));
    }

    [Fact]
    public async Task No_ar_vence_um_minuto_depois_do_fim_previsto_pelo_relogio_injetado()
    {
        var relogio = new RelogioFalso(Inicio);
        var store = new EstadoDoAoVivoStore(relogio, NullLogger<EstadoDoAoVivoStore>.Instance);
        await store.DefinirAsync(NoAr(Inicio, Inicio.AddHours(1)), default);

        relogio.Avancar(TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(1)));

        Assert.False((await store.ObterAsync(default))!.NoAr);
    }

    [Fact]
    public async Task Expirar_troca_o_no_ar_vencido_por_fora_do_ar_uma_unica_vez()
    {
        var relogio = new RelogioFalso(Inicio);
        var store = new EstadoDoAoVivoStore(relogio, NullLogger<EstadoDoAoVivoStore>.Instance);
        Assert.Null(await store.ExpirarVencidoAsync(default));
        var estado = NoAr(Inicio, Inicio.AddHours(1));
        await store.DefinirAsync(estado, default);

        relogio.Avancar(TimeSpan.FromMinutes(59));
        Assert.Null(await store.ExpirarVencidoAsync(default)); // ainda dentro do fim + 1 min

        relogio.Avancar(TimeSpan.FromMinutes(2));
        var troca = await store.ExpirarVencidoAsync(default);

        Assert.Equal(estado, troca!.Antes);
        Assert.False(troca.Depois.NoAr);
        Assert.Null(await store.ExpirarVencidoAsync(default));
    }

    [RedisFact]
    public async Task Com_Redis_duas_replicas_expiram_o_mesmo_vencido_e_so_uma_publica()
    {
        var relogio = new RelogioFalso(Inicio);
        var a = new EstadoDoAoVivoStore(relogio, NullLogger<EstadoDoAoVivoStore>.Instance, RedisDeTeste.Multiplexador);
        var b = new EstadoDoAoVivoStore(relogio, NullLogger<EstadoDoAoVivoStore>.Instance, RedisDeTeste.Multiplexador);
        var estado = NoAr(Inicio, Inicio.AddHours(1));
        await a.DefinirAsync(estado, default);
        relogio.Avancar(TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(2)));

        var trocas = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => (i % 2 == 0 ? a : b).ExpirarVencidoAsync(default)));

        Assert.Single(trocas, t => t is not null);
        Assert.False((await b.ObterAsync(default))!.NoAr);
        Assert.Null(await a.ExpirarVencidoAsync(default));
    }

    [RedisFact]
    public async Task Com_Redis_um_Conectado_novo_depois_da_leitura_nao_e_sobrescrito_pela_expiracao()
    {
        var relogio = new RelogioFalso(Inicio);
        var store = new EstadoDoAoVivoStore(relogio, NullLogger<EstadoDoAoVivoStore>.Instance, RedisDeTeste.Multiplexador);
        await store.DefinirAsync(NoAr(Inicio, Inicio.AddHours(1)), default);
        relogio.Avancar(TimeSpan.FromHours(2));
        var novo = NoAr(relogio.UtcNow, relogio.UtcNow.AddHours(1));
        await store.DefinirAsync(novo, default); // o Conectado novo chegou antes da passada

        Assert.Null(await store.ExpirarVencidoAsync(default));
        Assert.Equal(novo, await store.ObterAsync(default));
    }
}
