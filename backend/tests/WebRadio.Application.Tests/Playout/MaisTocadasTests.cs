using WebRadio.Application.Features.Relatorios;
using Xunit;

namespace WebRadio.Application.Tests.Playout;

/// <summary>E4-A02 (02 §11): as entradas do relatório; a agregação em si é da procedure (teste com SQL real na Infrastructure).</summary>
public sealed class MaisTocadasTests
{
    private static readonly DateTime De = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly MaisTocadasValidator Validador = new();

    [Fact]
    public void Periodo_de_ate_366_dias_e_top_de_1_a_100_valem()
    {
        Assert.True(Validador.Validate(new MaisTocadasQuery(De, De.AddDays(30), 20)).IsValid);
        Assert.True(Validador.Validate(new MaisTocadasQuery(De, De.AddDays(366), 1)).IsValid);
        Assert.True(Validador.Validate(new MaisTocadasQuery(De, De.AddDays(1), 100)).IsValid);
        Assert.Equal(20, new MaisTocadasQuery(De, De.AddDays(1)).Top); // padrão
    }

    [Fact]
    public void Fora_disso_e_400()
    {
        Assert.False(Validador.Validate(new MaisTocadasQuery(null, De.AddDays(1))).IsValid);
        Assert.False(Validador.Validate(new MaisTocadasQuery(De, null)).IsValid);
        Assert.False(Validador.Validate(new MaisTocadasQuery(De, De)).IsValid); // ate é exclusivo: período vazio
        Assert.False(Validador.Validate(new MaisTocadasQuery(De.AddDays(2), De)).IsValid);
        Assert.False(Validador.Validate(new MaisTocadasQuery(De, De.AddDays(367))).IsValid);
        Assert.False(Validador.Validate(new MaisTocadasQuery(De, De.AddDays(5), 0)).IsValid);
        Assert.False(Validador.Validate(new MaisTocadasQuery(De, De.AddDays(5), 101)).IsValid);
    }
}
