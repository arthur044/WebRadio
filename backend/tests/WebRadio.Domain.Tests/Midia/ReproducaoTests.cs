using WebRadio.Domain.Midia;
using Xunit;

namespace WebRadio.Domain.Tests.Midia;

public class ReproducaoTests
{
    private static readonly DateTime Agora = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Construtor_aceita_pedido_e_programa_opcionais()
    {
        var reproducao = new Reproducao(Guid.NewGuid(), pedidoId: null, programaId: null, Agora);
        Assert.Equal(Agora, reproducao.IniciadoEmUtc);
    }

    [Fact]
    public void Construtor_rejeita_data_nao_utc()
    {
        Assert.Throws<ArgumentException>(() => new Reproducao(Guid.NewGuid(), null, null, DateTime.Now));
    }
}
