using WebRadio.Domain.Enums;
using Xunit;

namespace WebRadio.Domain.Tests;

/// <summary>
/// Os valores do enum são o contrato com o banco (01 §1, <c>CK_Programa_Status</c>, índice filtrado <c>IX_Programa_Worker</c>
/// <c>WHERE Status IN (1, 2)</c>) e com o SQL do Worker, que os monta a partir do enum. Mudar um número sem a migration quebraria a
/// grade em silêncio: este teste é o aviso.
/// </summary>
public class StatusProgramaTests
{
    [Theory]
    [InlineData(StatusPrograma.Agendado, 1)]
    [InlineData(StatusPrograma.AoVivo, 2)]
    [InlineData(StatusPrograma.Finalizado, 3)]
    [InlineData(StatusPrograma.Cancelado, 4)]
    public void Os_valores_do_enum_sao_os_do_banco(StatusPrograma status, int esperado) => Assert.Equal(esperado, (int)status);

    [Fact]
    public void Nao_ha_outro_status_alem_dos_quatro_do_CK_Programa_Status()
        => Assert.Equal([1, 2, 3, 4], Enum.GetValues<StatusPrograma>().Select(s => (int)s).Order().ToArray());
}
