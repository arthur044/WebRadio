using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Infrastructure.Seguranca;
using Xunit;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Auth;

public class MemoryLoginThrottleTests
{
    private readonly RelogioFalso _relogio = new(Agora);
    private readonly MemoryLoginThrottle _t;
    private static readonly byte[] Ip2 = Enumerable.Repeat((byte)2, 32).ToArray();

    public MemoryLoginThrottleTests() => _t = new(_relogio, new IpHasher(new byte[32]), NullLogger<MemoryLoginThrottle>.Instance);

    private async Task Falhar(int n, string email = "A@X.COM", byte[]? ip = null)
    {
        for (var i = 0; i < n; i++) await _t.RegistrarFalhaAsync(email, ip ?? IpHash, default);
    }

    [Fact]
    public async Task Cinco_falhas_do_mesmo_par_bloqueiam_por_15_min_e_depois_liberam()
    {
        await Falhar(4);
        Assert.False((await _t.AvaliarAsync("A@X.COM", IpHash, default)).Bloqueado);
        await Falhar(1);
        Assert.True((await _t.AvaliarAsync("A@X.COM", IpHash, default)).Bloqueado);

        _relogio.UtcNow = Agora.AddMinutes(14);
        Assert.True((await _t.AvaliarAsync("A@X.COM", IpHash, default)).Bloqueado);
        _relogio.UtcNow = Agora.AddMinutes(16);
        Assert.False((await _t.AvaliarAsync("A@X.COM", IpHash, default)).Bloqueado);
    }

    [Fact]
    public async Task Outro_IP_ou_outra_conta_nao_e_bloqueado()
    {
        await Falhar(5);
        Assert.False((await _t.AvaliarAsync("A@X.COM", Ip2, default)).Bloqueado);
        Assert.False((await _t.AvaliarAsync("B@X.COM", IpHash, default)).Bloqueado);
    }

    [Fact]
    public async Task Sucesso_zera_o_par()
    {
        await Falhar(4);
        await _t.LimparAsync("A@X.COM", IpHash, default);
        await Falhar(4);
        Assert.False((await _t.AvaliarAsync("A@X.COM", IpHash, default)).Bloqueado);
    }

    [Fact]
    public async Task Cinquenta_falhas_na_conta_de_IPs_diferentes_dao_atraso_mas_nao_bloqueiam_o_IP_novo()
    {
        for (var i = 0; i < 50; i++)
            await Falhar(1, ip: BitConverter.GetBytes(i).Concat(new byte[28]).ToArray());

        var novo = await _t.AvaliarAsync("A@X.COM", Ip2, default);
        Assert.False(novo.Bloqueado);                 // atacante não tranca o Admin para quem vem de outro IP
        Assert.True(novo.Atraso > TimeSpan.Zero);     // só atraso progressivo

        _relogio.UtcNow = Agora.AddHours(2);
        Assert.Equal(TimeSpan.Zero, (await _t.AvaliarAsync("A@X.COM", Ip2, default)).Atraso);
    }

    [Fact]
    public async Task Teto_duro_de_entradas_a_memoria_nao_cresce_com_emails_inventados()
    {
        for (var i = 0; i < MemoryLoginThrottle.LimiteDeEntradas + 1000; i++)
        {
            _relogio.UtcNow = Agora.AddMilliseconds(i); // limpeza "cheia" roda no máximo 1x/s
            await _t.RegistrarFalhaAsync($"U{i}@X.COM", IpHash, default);
        }

        Assert.True(_t.Entradas <= MemoryLoginThrottle.LimiteDeEntradas, $"Entradas = {_t.Entradas}");
    }

    [Fact]
    public async Task Limpeza_por_tempo_remove_entradas_vencidas_sem_custo_por_falha()
    {
        await Falhar(1, "VELHO@X.COM");
        var antes = _t.Entradas;
        _relogio.UtcNow = Agora.AddHours(3);
        await Falhar(1, "NOVO@X.COM");
        Assert.Equal(antes, _t.Entradas); // as 2 entradas antigas saíram (≥ 1 min desde a última limpeza), entraram 2 novas
    }
}
