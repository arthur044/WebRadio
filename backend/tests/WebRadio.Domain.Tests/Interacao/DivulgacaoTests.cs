using WebRadio.Domain.Interacao;
using Xunit;

namespace WebRadio.Domain.Tests.Interacao;

public class DivulgacaoTests
{
    private static readonly DateTime Agora = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private static Divulgacao Criar(DateTime? inicio = null, DateTime? fim = null, byte prioridade = 50, bool ativo = true) =>
        new(Guid.NewGuid(), "Título", "Mensagem", null, "https://example.com", prioridade, inicio, fim, Guid.NewGuid(), Agora);

    [Fact]
    public void Construtor_rejeita_link_sem_https()
    {
        Assert.Throws<ArgumentException>(() =>
            new Divulgacao(Guid.NewGuid(), "T", "M", null, "http://inseguro.com", 0, null, null, Guid.NewGuid(), Agora));
    }

    [Fact]
    public void Construtor_rejeita_prioridade_acima_de_100()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Criar(prioridade: 101));
    }

    [Fact]
    public void Construtor_rejeita_janela_invertida()
    {
        Assert.Throws<ArgumentException>(() => Criar(inicio: Agora.AddDays(1), fim: Agora));
    }

    [Fact]
    public void EstaAtivaParaPublico_sem_janela_e_ativo_retorna_true()
    {
        var divulgacao = Criar();
        Assert.True(divulgacao.EstaAtivaParaPublico(Agora));
    }

    [Fact]
    public void EstaAtivaParaPublico_fora_da_janela_retorna_false()
    {
        var divulgacao = Criar(inicio: Agora.AddDays(1), fim: Agora.AddDays(2));
        Assert.False(divulgacao.EstaAtivaParaPublico(Agora));
    }

    [Fact]
    public void EstaAtivaParaPublico_desativada_retorna_false_mesmo_dentro_da_janela()
    {
        var divulgacao = Criar(inicio: Agora.AddDays(-1), fim: Agora.AddDays(1));
        divulgacao.Desativar(Agora);

        Assert.False(divulgacao.EstaAtivaParaPublico(Agora));
    }

    [Fact]
    public void Fim_da_janela_e_exclusivo()
    {
        var divulgacao = Criar(inicio: Agora.AddDays(-1), fim: Agora);
        Assert.False(divulgacao.EstaAtivaParaPublico(Agora));
    }

    [Fact]
    public void Ativar_depois_de_Desativar_volta_a_aparecer()
    {
        var divulgacao = Criar();
        divulgacao.Desativar(Agora.AddMinutes(1));
        divulgacao.Ativar(Agora.AddMinutes(2));

        Assert.True(divulgacao.EstaAtivaParaPublico(Agora.AddMinutes(3)));
    }

    [Fact]
    public void Editar_aplica_novos_valores()
    {
        var divulgacao = Criar();

        divulgacao.Editar("Novo título", "Nova mensagem", null, "https://novo.com", 10, null, null, Agora.AddMinutes(1));

        Assert.Equal("Novo título", divulgacao.Titulo);
        Assert.Equal(10, divulgacao.Prioridade);
    }
}
