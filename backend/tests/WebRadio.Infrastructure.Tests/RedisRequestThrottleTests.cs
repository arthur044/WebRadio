using StackExchange.Redis;
using WebRadio.Infrastructure.Seguranca;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>Piso em 0 do <c>IncrementarPorAsync</c> em Redis de verdade (o Lua não existe no dublê). Issue #140.</summary>
public class RedisRequestThrottleTests
{
    private static readonly TimeSpan Janela = TimeSpan.FromMinutes(5);

    private static string Chave() => $"teste-piso:{Guid.NewGuid():N}";

    [RedisFact]
    public async Task Devolver_numa_chave_que_nao_existe_mais_na_virada_da_hora_fica_em_0_e_nao_negativo()
    {
        var t = new RedisRequestThrottle(RedisDeTeste.Multiplexador);
        var chave = Chave();

        Assert.Equal(0, await t.IncrementarPorAsync(chave, -250L * 1024 * 1024, Janela));
        Assert.Equal(0, await t.LerAsync(chave));
        Assert.Equal(1, await t.IncrementarPorAsync(chave, 1, Janela)); // o próximo uso começa de 0, sem "crédito" negativo
    }

    [RedisFact]
    public async Task Devolver_mais_do_que_tem_zera_sem_perder_o_TTL()
    {
        var t = new RedisRequestThrottle(RedisDeTeste.Multiplexador);
        var chave = Chave();
        await t.IncrementarPorAsync(chave, 10, Janela);

        Assert.Equal(0, await t.IncrementarPorAsync(chave, -50, Janela));

        Assert.Equal(0, await t.LerAsync(chave));
        var ttl = await t.TempoRestanteAsync(chave);
        Assert.NotNull(ttl); // a chave segue com a janela original: não vira uma chave eterna
        Assert.InRange(ttl!.Value, TimeSpan.FromSeconds(1), Janela);
        await t.RemoverAsync(chave);
    }

    [RedisFact]
    public async Task Devolucao_dentro_do_saldo_continua_subtraindo()
    {
        var t = new RedisRequestThrottle(RedisDeTeste.Multiplexador);
        var chave = Chave();
        await t.IncrementarPorAsync(chave, 10, Janela);

        Assert.Equal(7, await t.IncrementarPorAsync(chave, -3, Janela));
        await t.RemoverAsync(chave);
    }
}
