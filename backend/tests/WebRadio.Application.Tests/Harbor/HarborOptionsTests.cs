using WebRadio.Application.Features.Harbor;
using Xunit;

namespace WebRadio.Application.Tests.Harbor;

public class HarborOptionsTests
{
    [Fact]
    public void Sem_host_configurado_a_conexao_tem_host_null_porta_8005_e_mount_live()
    {
        Assert.Equal(new ConexaoDoHarbor(null, 8005, "/live"), new HarborOptions().Conexao());
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("radio.example.com", "radio.example.com")]
    [InlineData("  10.8.0.1 ", "10.8.0.1")]
    public void Host_vazio_vira_null_e_o_resto_perde_os_espacos(string configurado, string? esperado)
    {
        Assert.Equal(esperado, new HarborOptions { HostPublico = configurado }.Conexao().Host);
    }

    [Theory]
    [InlineData(null, 8005, true)]
    [InlineData("radio.example.com", 8005, true)]
    [InlineData("10.8.0.1", 65535, true)]
    [InlineData("[fd00::1]", 1, true)]
    [InlineData("radio.example.com", 0, false)]
    [InlineData("radio.example.com", 65536, false)]
    [InlineData("radio example.com", 8005, false)]
    [InlineData("radio.example.com/<script>", 8005, false)]
    [InlineData("radio\nexample.com", 8005, false)]
    public void O_validador_recusa_host_estranho_e_porta_fora_de_1_a_65535(string? host, int porta, bool valido)
    {
        var r = new HarborOptionsValidator().Validate(null, new HarborOptions { HostPublico = host, PortaPublica = porta });

        Assert.Equal(valido, r.Succeeded);
    }
}
