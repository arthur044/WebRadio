using Xunit;

namespace WebRadio.Infrastructure.Tests.Suporte;

/// <summary>O suporte de teste decide se os [SqlFact] rodam, pulam ou falham: um erro aqui vira verde falso no CI.</summary>
public class BancoDeTesteTests
{
    private const string Configurado = "Server=sql-do-ci";
    private const string LocalDb = "Server=(localdb)";

    private static Action<string> SoResponde(params string[] vivos) =>
        cs =>
        {
            if (!vivos.Contains(cs)) throw new InvalidOperationException("servidor fora do ar (simulado)");
        };

    [Fact]
    public void Variavel_definida_e_servidor_de_pe_usa_o_servidor_configurado()
        => Assert.Equal(Configurado, BancoDeTeste.EscolherServidor(Configurado, LocalDb, SoResponde(Configurado, LocalDb)));

    [Fact]
    public void Variavel_definida_e_servidor_fora_FALHA_e_nao_cai_para_o_LocalDB()
    {
        var erro = Assert.Throws<InvalidOperationException>(() => BancoDeTeste.EscolherServidor(Configurado, LocalDb, SoResponde(LocalDb)));

        Assert.Contains("WEBRADIO_TEST_SQL", erro.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Configurado, erro.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Sem_variavel_usa_o_LocalDB_quando_ele_responde(string? configurado)
        => Assert.Equal(LocalDb, BancoDeTeste.EscolherServidor(configurado, LocalDb, SoResponde(LocalDb)));

    [Fact]
    public void Sem_variavel_e_sem_LocalDB_devolve_null_e_os_testes_pulam()
    {
        Assert.Null(BancoDeTeste.EscolherServidor(null, LocalDb, SoResponde()));
        Assert.Null(BancoDeTeste.EscolherServidor(null, null, SoResponde()));
    }

    [Fact]
    public void Sonda_do_LocalDB_em_plataforma_sem_suporte_nao_estoura()
        => Assert.Null(BancoDeTeste.EscolherServidor(null, LocalDb, _ => throw new PlatformNotSupportedException()));
}
