using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Midia;
using Xunit;

namespace WebRadio.Domain.Tests;

/// <summary>E4-D13: falha de infraestrutura devolve o item à fila e a tentativa.</summary>
public class ArquivoMidiaDevolucaoTests
{
    private static readonly DateTime Agora = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private static ArquivoMidia EmAnalise(int tentativasAntes = 0)
    {
        var m = new ArquivoMidia(Guid.NewGuid(), "a.mp3", null, null, TipoMidia.Vinheta, "audio/mpeg", 1000, Guid.NewGuid(), Agora);
        m.ConcluirUpload(1000, "etag", Agora);
        for (var i = 0; i < tentativasAntes; i++)
        {
            m.IniciarAnalise(Agora);
            m.RegistrarFalhaProcessamento(Agora);
        }

        m.IniciarAnalise(Agora);
        return m;
    }

    [Fact]
    public void Devolve_a_tentativa_e_volta_a_Pendente_sem_lease()
    {
        var m = EmAnalise();
        Assert.Equal(1, m.TentativasSanitizacao);

        m.DevolverPorFalhaDeInfraestrutura();

        Assert.Equal(StatusSanitizacao.Pendente, m.StatusSanitizacao);
        Assert.Equal(0, m.TentativasSanitizacao);
        Assert.Null(m.EmAnaliseDesdeUtc);
    }

    [Fact]
    public void Quatro_ciclos_com_o_antivirus_parado_nunca_rejeitam_o_arquivo()
    {
        var m = EmAnalise();
        for (var i = 0; i < 4; i++)
        {
            m.DevolverPorFalhaDeInfraestrutura();
            m.IniciarAnalise(Agora);
        }

        m.DevolverPorFalhaDeInfraestrutura();
        Assert.Equal(StatusSanitizacao.Pendente, m.StatusSanitizacao);
        Assert.Equal(0, m.TentativasSanitizacao);
    }

    [Fact]
    public void Preserva_as_tentativas_de_falhas_anteriores_do_arquivo()
    {
        var m = EmAnalise(tentativasAntes: 1); // 1 falha do arquivo + esta tentativa = 2
        m.DevolverPorFalhaDeInfraestrutura();
        Assert.Equal(1, m.TentativasSanitizacao);
    }

    [Fact]
    public void So_vale_em_analise()
    {
        var m = new ArquivoMidia(Guid.NewGuid(), "a.mp3", null, null, TipoMidia.Vinheta, "audio/mpeg", 1000, Guid.NewGuid(), Agora);
        Assert.Throws<TransicaoInvalidaException>(() => m.DevolverPorFalhaDeInfraestrutura());
    }
}
