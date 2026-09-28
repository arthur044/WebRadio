using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Grade;
using Xunit;

namespace WebRadio.Domain.Tests.Grade;

public class ProgramaTests
{
    private static readonly DateTime Agora = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private static Programa Criar(DateTime? inicio = null, DateTime? fim = null, DateTime? agora = null) =>
        new(
            Guid.NewGuid(),
            "Manhã Fresca",
            "descrição",
            Guid.NewGuid(),
            inicio ?? Agora.AddHours(1),
            fim ?? Agora.AddHours(2),
            agora ?? Agora);

    [Fact]
    public void Construtor_comeca_Agendado()
    {
        var programa = Criar();
        Assert.Equal(StatusPrograma.Agendado, programa.Status);
    }

    [Theory]
    [InlineData(4)] // menor que o mínimo de 5 min
    [InlineData(13 * 60)] // maior que o máximo de 12h (em minutos)
    public void Construtor_rejeita_duracao_fora_do_limite(int minutos)
    {
        Assert.Throws<ArgumentException>(() =>
            Criar(inicio: Agora.AddHours(1), fim: Agora.AddHours(1).AddMinutes(minutos)));
    }

    [Fact]
    public void Construtor_rejeita_fim_antes_ou_igual_ao_inicio()
    {
        Assert.Throws<ArgumentException>(() => Criar(inicio: Agora.AddHours(2), fim: Agora.AddHours(2)));
    }

    [Fact]
    public void Construtor_rejeita_inicio_mais_de_1_minuto_no_passado()
    {
        Assert.Throws<ArgumentException>(() =>
            Criar(inicio: Agora.AddMinutes(-2), fim: Agora.AddMinutes(30), agora: Agora));
    }

    [Fact]
    public void Construtor_aceita_inicio_ate_1_minuto_no_passado()
    {
        var programa = Criar(inicio: Agora.AddSeconds(-30), fim: Agora.AddMinutes(30), agora: Agora);
        Assert.Equal(StatusPrograma.Agendado, programa.Status);
    }

    [Fact]
    public void IniciarAoVivo_dentro_da_janela_funciona()
    {
        var programa = Criar(inicio: Agora, fim: Agora.AddHours(1), agora: Agora);

        programa.IniciarAoVivo(Agora.AddMinutes(1));

        Assert.Equal(StatusPrograma.AoVivo, programa.Status);
    }

    [Fact]
    public void IniciarAoVivo_antes_do_inicio_lanca_TransicaoInvalida()
    {
        var programa = Criar(inicio: Agora.AddHours(1), fim: Agora.AddHours(2), agora: Agora);

        Assert.Throws<TransicaoInvalidaException>(() => programa.IniciarAoVivo(Agora));
    }

    [Fact]
    public void IniciarAoVivo_quando_ja_esta_AoVivo_lanca_TransicaoInvalida()
    {
        var programa = Criar(inicio: Agora, fim: Agora.AddHours(1), agora: Agora);
        programa.IniciarAoVivo(Agora.AddMinutes(1));

        Assert.Throws<TransicaoInvalidaException>(() => programa.IniciarAoVivo(Agora.AddMinutes(2)));
    }

    [Fact]
    public void FinalizarPorFimDeJanela_a_partir_de_AoVivo_funciona()
    {
        var programa = Criar(inicio: Agora, fim: Agora.AddHours(1), agora: Agora);
        programa.IniciarAoVivo(Agora.AddMinutes(1));

        programa.FinalizarPorFimDeJanela(Agora.AddHours(1).AddSeconds(1));

        Assert.Equal(StatusPrograma.Finalizado, programa.Status);
    }

    [Fact]
    public void FinalizarPorFimDeJanela_janela_perdida_direto_de_Agendado_funciona()
    {
        // Simula o worker que não rodou a tempo: Fim já passou e o programa nunca virou AoVivo.
        var programa = Criar(inicio: Agora, fim: Agora.AddMinutes(10), agora: Agora);

        programa.FinalizarPorFimDeJanela(Agora.AddHours(1));

        Assert.Equal(StatusPrograma.Finalizado, programa.Status);
    }

    [Fact]
    public void FinalizarPorFimDeJanela_antes_do_fim_lanca_TransicaoInvalida()
    {
        var programa = Criar(inicio: Agora, fim: Agora.AddHours(1), agora: Agora);

        Assert.Throws<TransicaoInvalidaException>(() => programa.FinalizarPorFimDeJanela(Agora.AddMinutes(30)));
    }

    [Fact]
    public void FinalizarPorFimDeJanela_a_partir_de_Cancelado_lanca_TransicaoInvalida()
    {
        var programa = Criar(inicio: Agora.AddHours(1), fim: Agora.AddHours(2), agora: Agora);
        programa.Cancelar(Agora.AddMinutes(1));

        Assert.Throws<TransicaoInvalidaException>(() => programa.FinalizarPorFimDeJanela(Agora.AddHours(3)));
    }

    [Fact]
    public void Encerrar_a_partir_de_AoVivo_ajusta_FimUtc_para_agora()
    {
        var programa = Criar(inicio: Agora, fim: Agora.AddHours(2), agora: Agora);
        programa.IniciarAoVivo(Agora.AddMinutes(1));

        programa.Encerrar(Agora.AddMinutes(30));

        Assert.Equal(StatusPrograma.Finalizado, programa.Status);
        Assert.Equal(Agora.AddMinutes(30), programa.FimUtc);
    }

    [Fact]
    public void Encerrar_a_partir_de_Agendado_lanca_TransicaoInvalida()
    {
        var programa = Criar(inicio: Agora.AddHours(1), fim: Agora.AddHours(2), agora: Agora);

        Assert.Throws<TransicaoInvalidaException>(() => programa.Encerrar(Agora.AddHours(1).AddMinutes(1)));
    }

    [Fact]
    public void Encerrar_a_partir_de_Finalizado_lanca_TransicaoInvalida()
    {
        var programa = Criar(inicio: Agora, fim: Agora.AddMinutes(10), agora: Agora);
        programa.FinalizarPorFimDeJanela(Agora.AddHours(1));

        Assert.Throws<TransicaoInvalidaException>(() => programa.Encerrar(Agora.AddHours(2)));
    }

    [Fact]
    public void Encerrar_com_agora_antes_ou_igual_ao_inicio_lanca_TransicaoInvalida()
    {
        var programa = Criar(inicio: Agora, fim: Agora.AddHours(1), agora: Agora);
        programa.IniciarAoVivo(Agora.AddMinutes(1));

        Assert.Throws<TransicaoInvalidaException>(() => programa.Encerrar(Agora));
    }

    [Fact]
    public void Cancelar_a_partir_de_Agendado_funciona()
    {
        var programa = Criar(inicio: Agora.AddHours(1), fim: Agora.AddHours(2), agora: Agora);

        programa.Cancelar(Agora.AddMinutes(1));

        Assert.Equal(StatusPrograma.Cancelado, programa.Status);
        Assert.NotNull(programa.CanceladoEmUtc);
    }

    [Fact]
    public void Cancelar_a_partir_de_AoVivo_lanca_TransicaoInvalida()
    {
        var programa = Criar(inicio: Agora, fim: Agora.AddHours(1), agora: Agora);
        programa.IniciarAoVivo(Agora.AddMinutes(1));

        Assert.Throws<TransicaoInvalidaException>(() => programa.Cancelar(Agora.AddMinutes(2)));
    }

    [Fact]
    public void Cancelar_a_partir_de_Finalizado_lanca_TransicaoInvalida()
    {
        var programa = Criar(inicio: Agora, fim: Agora.AddMinutes(10), agora: Agora);
        programa.FinalizarPorFimDeJanela(Agora.AddHours(1));

        Assert.Throws<TransicaoInvalidaException>(() => programa.Cancelar(Agora.AddHours(2)));
    }

    [Fact]
    public void Editar_a_partir_de_Agendado_funciona()
    {
        var programa = Criar(inicio: Agora.AddHours(1), fim: Agora.AddHours(2), agora: Agora);
        var novoLocutor = Guid.NewGuid();

        programa.Editar("Novo título", null, novoLocutor, Agora.AddHours(3), Agora.AddHours(4), Agora.AddMinutes(1));

        Assert.Equal("Novo título", programa.Titulo);
        Assert.Equal(novoLocutor, programa.LocutorId);
    }

    [Fact]
    public void Editar_a_partir_de_AoVivo_lanca_TransicaoInvalida()
    {
        var programa = Criar(inicio: Agora, fim: Agora.AddHours(1), agora: Agora);
        programa.IniciarAoVivo(Agora.AddMinutes(1));

        Assert.Throws<TransicaoInvalidaException>(() =>
            programa.Editar("X", null, Guid.NewGuid(), Agora.AddHours(2), Agora.AddHours(3), Agora.AddMinutes(2)));
    }
}
