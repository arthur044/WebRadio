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
    public void Encerrar_um_Agendado_cuja_janela_ja_comecou_funciona_com_o_Worker_parado()
    {
        // E2-F01: o Worker não passou o programa para AoVivo (parado), mas a janela está em curso: encerrar vale.
        var programa = Criar(inicio: Agora.AddMinutes(10), fim: Agora.AddHours(2), agora: Agora);

        programa.Encerrar(Agora.AddMinutes(40));

        Assert.Equal(StatusPrograma.Finalizado, programa.Status);
        Assert.Equal(Agora.AddMinutes(40), programa.FimUtc);
    }

    [Fact]
    public void Encerrar_um_Agendado_antes_do_inicio_lanca_TransicaoInvalida()
    {
        var programa = Criar(inicio: Agora.AddHours(1), fim: Agora.AddHours(2), agora: Agora);

        Assert.Throws<TransicaoInvalidaException>(() => programa.Encerrar(Agora.AddMinutes(59)));
    }

    [Theory]
    [InlineData(false)] // Agendado, Worker parado
    [InlineData(true)]  // AoVivo
    public void Encerrar_no_segundo_exato_do_inicio_deixa_FimUtc_um_segundo_depois_do_inicio(bool aoVivo)
    {
        // FimUtc = máx(agora, InicioUtc + 1 s): com FimUtc = InicioUtc o CK_Programa_Intervalo (Fim > Início) falharia.
        var inicio = Agora.AddHours(1);
        var programa = Criar(inicio: inicio, fim: inicio.AddHours(1), agora: Agora);
        if (aoVivo) programa.IniciarAoVivo(inicio);

        programa.Encerrar(inicio);

        Assert.Equal(StatusPrograma.Finalizado, programa.Status);
        Assert.Equal(inicio.AddSeconds(1), programa.FimUtc);
        Assert.True(programa.FimUtc > programa.InicioUtc);
    }

    [Fact]
    public void Encerrar_trunca_FimUtc_para_o_segundo_porque_a_coluna_e_datetime2_0()
    {
        // datetime2(0) ARREDONDA: 12:40:00.700 viraria 12:40:01, depois do "agora" de quem encerrou.
        var programa = Criar(inicio: Agora, fim: Agora.AddHours(2), agora: Agora);
        programa.IniciarAoVivo(Agora.AddMinutes(1));

        programa.Encerrar(Agora.AddMinutes(40).AddMilliseconds(700));

        Assert.Equal(Agora.AddMinutes(40), programa.FimUtc);
    }

    [Fact]
    public void Encerrar_depois_do_fim_previsto_nao_estende_a_janela()
    {
        // Janela já vencida (Worker parado há tempo): encerrar finaliza, mas nunca aumenta FimUtc (o limite de 12 h e o
        // histórico dependem dele).
        var programa = Criar(inicio: Agora.AddMinutes(10), fim: Agora.AddMinutes(40), agora: Agora);

        programa.Encerrar(Agora.AddHours(5));

        Assert.Equal(StatusPrograma.Finalizado, programa.Status);
        Assert.Equal(Agora.AddMinutes(40), programa.FimUtc);
    }

    [Fact]
    public void Encerrar_a_partir_de_Cancelado_lanca_TransicaoInvalida()
    {
        var programa = Criar(inicio: Agora.AddHours(1), fim: Agora.AddHours(2), agora: Agora);
        programa.Cancelar(Agora.AddMinutes(1));

        Assert.Throws<TransicaoInvalidaException>(() => programa.Encerrar(Agora.AddHours(1).AddMinutes(5)));
    }

    [Fact]
    public void Encerrar_a_partir_de_Finalizado_lanca_TransicaoInvalida()
    {
        var programa = Criar(inicio: Agora, fim: Agora.AddMinutes(10), agora: Agora);
        programa.FinalizarPorFimDeJanela(Agora.AddHours(1));

        Assert.Throws<TransicaoInvalidaException>(() => programa.Encerrar(Agora.AddHours(2)));
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
