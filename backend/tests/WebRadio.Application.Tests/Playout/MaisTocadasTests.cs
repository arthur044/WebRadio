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

    [Fact]
    public void Periodo_de_menos_de_um_segundo_e_400_porque_a_procedure_trabalha_em_segundos()
    {
        Assert.False(Validador.Validate(new MaisTocadasQuery(De.AddMilliseconds(100), De.AddMilliseconds(200))).IsValid);
        Assert.True(Validador.Validate(new MaisTocadasQuery(De, De.AddSeconds(1))).IsValid);
    }

    private sealed class RelatorioQueGuarda : IRelatorioDeReproducoes
    {
        public (DateTime De, DateTime Ate)? Recebido { get; private set; }

        public Task<IReadOnlyList<MidiaMaisTocadaDto>> MaisTocadasAsync(DateTime de, DateTime ate, int top, CancellationToken ct)
        {
            Recebido = (de, ate);
            return Task.FromResult<IReadOnlyList<MidiaMaisTocadaDto>>([]);
        }
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)] // "?de=2026-11-01" sem offset: é UTC, nunca a hora local do processo
    [InlineData(DateTimeKind.Utc)]
    public async Task De_e_ate_sem_offset_ou_em_UTC_chegam_ao_relatorio_como_o_mesmo_instante_UTC(DateTimeKind tipo)
    {
        var relatorio = new RelatorioQueGuarda();
        var de = DateTime.SpecifyKind(new DateTime(2026, 11, 1, 10, 0, 0), tipo);

        await new MaisTocadasHandler(relatorio).Handle(new MaisTocadasQuery(de, de.AddDays(1)), default);

        Assert.Equal((new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc)), relatorio.Recebido);
        Assert.Equal(DateTimeKind.Utc, relatorio.Recebido!.Value.De.Kind);
    }
}
