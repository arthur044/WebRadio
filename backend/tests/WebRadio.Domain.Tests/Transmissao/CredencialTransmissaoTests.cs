using WebRadio.Domain.Transmissao;
using Xunit;

namespace WebRadio.Domain.Tests.Transmissao;

public class CredencialTransmissaoTests
{
    private static readonly DateTime Agora = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private static CredencialTransmissao Criar() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new byte[32], new byte[16], Agora);

    [Fact]
    public void Nasce_ativa_e_sem_uso()
    {
        var c = Criar();

        Assert.True(c.EstaAtiva);
        Assert.Null(c.RevogadaEmUtc);
        Assert.Null(c.UltimoUsoEmUtc);
        Assert.Equal(Agora, c.CriadaEmUtc);
    }

    [Theory]
    [InlineData(31, 16)]
    [InlineData(32, 15)]
    public void Recusa_hash_ou_salt_de_tamanho_errado(int hash, int salt)
    {
        Assert.Throws<ArgumentException>(() =>
            new CredencialTransmissao(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new byte[hash], new byte[salt], Agora));
    }

    [Fact]
    public void Recusa_identificador_ou_usuario_vazio()
    {
        Assert.Throws<ArgumentException>(() => new CredencialTransmissao(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, new byte[32], new byte[16], Agora));
        Assert.Throws<ArgumentException>(() => new CredencialTransmissao(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), new byte[32], new byte[16], Agora));
    }

    [Fact]
    public void Recusa_data_que_nao_e_utc()
    {
        Assert.Throws<ArgumentException>(() =>
            new CredencialTransmissao(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new byte[32], new byte[16], DateTime.Now));
    }

    [Fact]
    public void Copia_hash_e_salt_para_nao_compartilhar_o_array()
    {
        var hash = new byte[32];
        var c = new CredencialTransmissao(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), hash, new byte[16], Agora);

        hash[0] = 1;

        Assert.Equal(0, c.SegredoHash[0]);
    }

    [Fact]
    public void Revogar_grava_a_data_e_e_idempotente()
    {
        var c = Criar();

        c.Revogar(Agora.AddMinutes(1));
        c.Revogar(Agora.AddMinutes(2));

        Assert.False(c.EstaAtiva);
        Assert.Equal(Agora.AddMinutes(1), c.RevogadaEmUtc);
    }

    [Fact]
    public void RegistrarUso_grava_o_ultimo_uso()
    {
        var c = Criar();

        c.RegistrarUso(Agora.AddMinutes(5));

        Assert.Equal(Agora.AddMinutes(5), c.UltimoUsoEmUtc);
    }

    [Fact]
    public void ToString_nao_imprime_hash_nem_salt()
    {
        var c = Criar();

        Assert.Equal($"CredencialTransmissao {c.Id}", c.ToString());
    }
}
