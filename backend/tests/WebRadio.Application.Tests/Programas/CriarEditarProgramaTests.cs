using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Features.Programas;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using Xunit;
using static WebRadio.Application.Tests.Auth.AuthFixture;
using static WebRadio.Application.Tests.Programas.ProgramasFixture;

namespace WebRadio.Application.Tests.Programas;

/// <summary>E2-F01: criar e editar, com a checagem de sobreposição do Fluxo A (o lock em si é testado com SQL real).</summary>
public class CriarEditarProgramaTests
{
    private readonly ProgramasFixture _f = new();

    [Fact]
    public async Task Cria_Agendado_com_o_locutor_e_devolve_o_dto_com_versao()
    {
        var dto = await _f.CriarAsync("Manhã Fresca", 60, 120);

        Assert.Equal("Agendado", dto.Status);
        Assert.Equal(_f.Locutor.Id, dto.Locutor.Id);
        Assert.Equal(_f.Locutor.Nome, dto.Locutor.Nome);
        Assert.Equal(Agora.AddMinutes(60), dto.InicioUtc);
        Assert.Single(await _f.Db.Programas.ToListAsync());
    }

    [Fact]
    public async Task Intervalo_que_se_cruza_com_um_Agendado_e_conflito_e_traz_o_id_do_conflitante()
    {
        var existente = await _f.CriarAsync("A", 60, 120);

        var e = await Assert.ThrowsAsync<ConflitoDeGradeException>(() => _f.CriarAsync("B", 90, 150));

        Assert.Equal(existente.Id, e.ProgramaConflitanteId);
        Assert.Single(await _f.Db.Programas.ToListAsync()); // nada foi gravado
    }

    [Theory]
    [InlineData(0, 60)]    // termina exatamente quando o outro começa: [a, b) e [b, c) não conflitam
    [InlineData(120, 180)] // começa exatamente quando o outro termina
    public async Task Intervalos_encostados_nao_conflitam(int inicio, int fim)
    {
        await _f.CriarAsync("A", 60, 120);

        var dto = await _f.CriarAsync("B", inicio, fim);

        Assert.Equal("Agendado", dto.Status);
    }

    [Fact]
    public async Task Programa_Finalizado_ou_Cancelado_nao_conta_para_o_conflito()
    {
        var finalizado = await _f.CriarAsync("A", 60, 120);
        var cancelado = await _f.CriarAsync("B", 200, 260);
        await _f.ForcarAsync(finalizado.Id, p => p.FinalizarPorFimDeJanela(p.FimUtc));
        await _f.ForcarAsync(cancelado.Id, p => p.Cancelar(Agora));

        await _f.CriarAsync("C", 60, 120);
        await _f.CriarAsync("D", 200, 260);

        Assert.Equal(4, await _f.Db.Programas.CountAsync());
    }

    [Fact]
    public async Task Programa_AoVivo_conta_para_o_conflito()
    {
        var p = await _f.CriarAsync("Agora", 0 + 1, 60);
        await _f.ForcarAsync(p.Id, x => x.IniciarAoVivo(Agora.AddMinutes(1)));

        await Assert.ThrowsAsync<ConflitoDeGradeException>(() => _f.CriarAsync("B", 30, 90));
    }

    [Fact]
    public async Task Programa_de_12_horas_que_comeca_antes_da_faixa_ainda_e_visto_pela_consulta()
    {
        // O limite InicioUtc > novo.Inicio - 12 h é só otimização de seek: um programa de 12 h cruza o novo mesmo assim.
        var longo = await _f.CriarAsync("Maratona", 1, 12 * 60 + 1);

        var e = await Assert.ThrowsAsync<ConflitoDeGradeException>(() => _f.CriarAsync("B", 12 * 60, 12 * 60 + 30));
        Assert.Equal(longo.Id, e.ProgramaConflitanteId);
    }

    [Theory]
    [InlineData("locutor-inexistente")]
    [InlineData("ouvinte")]
    [InlineData("inativo")]
    public async Task Locutor_invalido_vira_erro_de_validacao_e_nao_grava(string caso)
    {
        var locutorId = caso switch
        {
            "ouvinte" => CriarUsuario(_f.Db, new WebRadio.Infrastructure.Seguranca.SenhaHasher(), "ouvinte@example.com", role: Role.Ouvinte).Id,
            "inativo" => CriarUsuario(_f.Db, new WebRadio.Infrastructure.Seguranca.SenhaHasher(), "inativo@example.com", ativo: false).Id,
            _ => Guid.NewGuid(),
        };

        var e = await Assert.ThrowsAsync<ValidationException>(() => _f.CriarAsync("A", 60, 120, locutorId));

        Assert.Contains(e.Errors, x => x.PropertyName == "locutorId");
        Assert.Empty(await _f.Db.Programas.ToListAsync());
    }

    [Fact]
    public async Task Admin_pode_ser_o_locutor_do_programa()
        => Assert.Equal(_f.Admin.Id, (await _f.CriarAsync("Do Admin", 60, 120, _f.Admin.Id)).Locutor.Id);

    // ---- validação do corpo (o Domain ainda lança ArgumentException; a Application responde 400 antes) ----

    private ProgramaCorpoValidator<CriarProgramaCommand> Validador => new CriarProgramaValidator(_f.Relogio);

    private static CriarProgramaCommand Cmd(string titulo = "Título", string? descricao = null, DateTime? inicio = null, DateTime? fim = null) =>
        new(titulo, descricao, Guid.NewGuid(), inicio ?? Agora.AddHours(1), fim ?? Agora.AddHours(2));

    [Fact]
    public void Corpo_valido_passa() => Assert.True(Validador.Validate(Cmd()).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Titulo_vazio_e_recusado(string titulo) => Assert.False(Validador.Validate(Cmd(titulo)).IsValid);

    [Fact]
    public void Titulo_com_mais_de_120_e_descricao_com_mais_de_1000_sao_recusados()
    {
        Assert.False(Validador.Validate(Cmd(new string('t', 121))).IsValid);
        Assert.True(Validador.Validate(Cmd(new string('t', 120))).IsValid);
        Assert.False(Validador.Validate(Cmd(descricao: new string('d', 1001))).IsValid);
        Assert.True(Validador.Validate(Cmd(descricao: new string('d', 1000))).IsValid);
    }

    [Theory]
    [InlineData(4)]        // menos de 5 min
    [InlineData(13 * 60)]  // mais de 12 h
    [InlineData(0)]
    [InlineData(-10)]
    public void Duracao_fora_de_5_min_a_12_h_e_recusada(int minutos)
        => Assert.False(Validador.Validate(Cmd(inicio: Agora.AddHours(1), fim: Agora.AddHours(1).AddMinutes(minutos))).IsValid);

    [Theory]
    [InlineData(5)]
    [InlineData(12 * 60)]
    public void Duracao_nos_limites_e_aceita(int minutos)
        => Assert.True(Validador.Validate(Cmd(inicio: Agora.AddHours(1), fim: Agora.AddHours(1).AddMinutes(minutos))).IsValid);

    [Fact]
    public void Inicio_ha_mais_de_1_minuto_no_passado_e_recusado_e_ate_1_minuto_e_tolerado()
    {
        Assert.False(Validador.Validate(Cmd(inicio: Agora.AddMinutes(-2), fim: Agora.AddMinutes(60))).IsValid);
        Assert.True(Validador.Validate(Cmd(inicio: Agora.AddSeconds(-59), fim: Agora.AddMinutes(60))).IsValid);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Horario_que_nao_e_UTC_e_recusado(DateTimeKind tipo)
    {
        var inicio = DateTime.SpecifyKind(Agora.AddHours(1), tipo);

        Assert.False(Validador.Validate(Cmd(inicio: inicio)).IsValid);
        Assert.False(Validador.Validate(Cmd(fim: DateTime.SpecifyKind(Agora.AddHours(2), tipo))).IsValid);
    }

    [Fact]
    public void Horario_com_fracao_de_segundo_e_recusado_porque_a_precisao_e_de_segundo()
        => Assert.False(Validador.Validate(Cmd(inicio: Agora.AddHours(1).AddMilliseconds(500))).IsValid);

    [Fact]
    public void Locutor_vazio_e_recusado()
        => Assert.False(Validador.Validate(Cmd() with { LocutorId = Guid.Empty }).IsValid);

    // ---- editar ----

    [Fact]
    public async Task Edita_um_Agendado_e_nao_conflita_com_o_proprio_horario()
    {
        var p = await _f.CriarAsync("A", 60, 120);
        var (i, f) = Em(70, 130); // cruza o horário ANTIGO dele mesmo: o próprio Id é excluído da consulta

        var dto = await _f.Editar.Handle(new EditarProgramaCommand(p.Id, "A2", "nova", _f.OutroLocutor.Id, i, f, Convert.FromBase64String(p.Versao)), default);

        Assert.Equal("A2", dto.Titulo);
        Assert.Equal(_f.OutroLocutor.Id, dto.Locutor.Id);
        Assert.Equal(i, dto.InicioUtc);
    }

    [Fact]
    public async Task Editar_para_um_horario_que_cruza_outro_programa_e_conflito()
    {
        var a = await _f.CriarAsync("A", 60, 120);
        var b = await _f.CriarAsync("B", 200, 260);
        var (i, f) = Em(100, 220);

        var e = await Assert.ThrowsAsync<ConflitoDeGradeException>(() =>
            _f.Editar.Handle(new EditarProgramaCommand(b.Id, "B", null, _f.Locutor.Id, i, f, Convert.FromBase64String(b.Versao)), default));

        Assert.Equal(a.Id, e.ProgramaConflitanteId);
    }

    [Fact]
    public async Task Editar_com_versao_antiga_e_412_e_programa_inexistente_e_404()
    {
        var p = await _f.CriarAsync("A", 60, 120);
        var (i, f) = Em(60, 120);

        await Assert.ThrowsAsync<VersaoDesatualizadaException>(() =>
            _f.Editar.Handle(new EditarProgramaCommand(p.Id, "A", null, _f.Locutor.Id, i, f, [1, 2, 3]), default));
        await Assert.ThrowsAsync<NaoEncontradoException>(() =>
            _f.Editar.Handle(new EditarProgramaCommand(Guid.NewGuid(), "A", null, _f.Locutor.Id, i, f, Convert.FromBase64String(p.Versao)), default));
    }

    [Theory]
    [InlineData("AoVivo")]
    [InlineData("Finalizado")]
    [InlineData("Cancelado")]
    public async Task So_Agendado_pode_ser_editado(string status)
    {
        var p = await _f.CriarAsync("A", 1, 60);
        await _f.ForcarAsync(p.Id, x =>
        {
            if (status == "AoVivo") x.IniciarAoVivo(Agora.AddMinutes(1));
            else if (status == "Finalizado") x.FinalizarPorFimDeJanela(x.FimUtc);
            else x.Cancelar(Agora);
        });
        var atual = await _f.PorId.Handle(new ProgramaPorIdQuery(p.Id), default);
        var (i, f) = Em(1, 60);

        await Assert.ThrowsAsync<TransicaoInvalidaException>(() =>
            _f.Editar.Handle(new EditarProgramaCommand(p.Id, "X", null, _f.Locutor.Id, i, f, Convert.FromBase64String(atual.Versao)), default));
    }
}
