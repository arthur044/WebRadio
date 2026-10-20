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
}
