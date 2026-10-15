using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Features.Programas;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using Xunit;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Programas;

/// <summary>E2-F01: encerrar e cancelar gravam <c>ProgramaStatusAlterado</c> no outbox, na mesma transação, uma vez só.</summary>
public class EncerrarCancelarProgramaTests
{
    private readonly ProgramasFixture _f = new();

    private async Task<ProgramaDto> AoVivoAsync()
    {
        var p = await _f.CriarAsync("No ar", 0, 120);
        await _f.ForcarAsync(p.Id, x => x.IniciarAoVivo(Agora.AddSeconds(1)));
        _f.Db.ChangeTracker.Clear();
        return p;
    }

    [Fact]
    public async Task Locutor_dono_encerra_o_AoVivo_FimUtc_vira_agora_e_um_evento_vai_para_todos()
    {
        var p = await AoVivoAsync();
        _f.Relogio.UtcNow = Agora.AddMinutes(30);

        var dto = await _f.Encerrar.Handle(new EncerrarProgramaCommand(p.Id, _f.Locutor.Id, SolicitanteEhAdmin: false), default);

        Assert.Equal("Finalizado", dto.Status);
        Assert.Equal(Agora.AddMinutes(30), dto.FimUtc);
        var evento = Assert.Single(_f.Eventos());
        Assert.Equal("ProgramaStatusAlterado", evento.Tipo);
        Assert.True(evento.ParaTodos);
        Assert.Equal(p.Id, evento.Dados.GetProperty("programaId").GetGuid());
        Assert.Equal("No ar", evento.Dados.GetProperty("titulo").GetString());
        Assert.Equal("Finalizado", evento.Dados.GetProperty("status").GetString());
        Assert.Equal(_f.Locutor.Nome, evento.Dados.GetProperty("locutorNome").GetString());
    }

    [Fact]
    public async Task Encerrar_um_Agendado_com_a_janela_em_curso_funciona_com_o_Worker_parado()
    {
        // Nunca passou para AoVivo (ninguém rodou o Worker), mas a janela começou: o locutor que está no ar precisa conseguir encerrar.
        var p = await _f.CriarAsync("Sem Worker", 0, 120);
        _f.Relogio.UtcNow = Agora.AddMinutes(10);

        var dto = await _f.Encerrar.Handle(new EncerrarProgramaCommand(p.Id, _f.Locutor.Id, false), default);

        Assert.Equal("Finalizado", dto.Status);
        Assert.Equal(Agora.AddMinutes(10), dto.FimUtc);
        Assert.Single(_f.Eventos());
    }

    [Fact]
    public async Task Encerrar_no_segundo_exato_do_inicio_deixa_FimUtc_um_segundo_depois()
    {
        var p = await _f.CriarAsync("Agora mesmo", 0, 120);
        _f.Relogio.UtcNow = Agora; // agora == InicioUtc

        var dto = await _f.Encerrar.Handle(new EncerrarProgramaCommand(p.Id, _f.Locutor.Id, false), default);

        Assert.Equal(Agora.AddSeconds(1), dto.FimUtc);
    }

    [Fact]
    public async Task Encerrar_um_Agendado_antes_do_inicio_e_transicao_invalida_e_nao_grava_evento()
    {
        var p = await _f.CriarAsync("Amanhã", 60, 120);

        await Assert.ThrowsAsync<TransicaoInvalidaException>(() =>
            _f.Encerrar.Handle(new EncerrarProgramaCommand(p.Id, _f.Locutor.Id, false), default));
        Assert.Empty(_f.Eventos());
    }

    [Fact]
    public async Task Outro_locutor_nao_encerra_o_programa_alheio_mas_o_Admin_encerra()
    {
        var p = await AoVivoAsync();
        _f.Relogio.UtcNow = Agora.AddMinutes(5);

        await Assert.ThrowsAsync<AcessoNegadoException>(() =>
            _f.Encerrar.Handle(new EncerrarProgramaCommand(p.Id, _f.OutroLocutor.Id, false), default));
        Assert.Empty(_f.Eventos());

        var dto = await _f.Encerrar.Handle(new EncerrarProgramaCommand(p.Id, _f.Admin.Id, SolicitanteEhAdmin: true), default);
        Assert.Equal("Finalizado", dto.Status);
    }

    [Fact]
    public async Task Encerrar_duas_vezes_a_segunda_e_transicao_invalida_e_o_outbox_fica_com_um_evento_so()
    {
        var p = await AoVivoAsync();
        _f.Relogio.UtcNow = Agora.AddMinutes(5);
        await _f.Encerrar.Handle(new EncerrarProgramaCommand(p.Id, _f.Locutor.Id, false), default);

        await Assert.ThrowsAsync<TransicaoInvalidaException>(() =>
            _f.Encerrar.Handle(new EncerrarProgramaCommand(p.Id, _f.Locutor.Id, false), default));
        Assert.Single(_f.Eventos());
    }

    [Fact]
    public async Task Encerrar_programa_inexistente_e_404()
        => await Assert.ThrowsAsync<NaoEncontradoException>(() =>
            _f.Encerrar.Handle(new EncerrarProgramaCommand(Guid.NewGuid(), _f.Admin.Id, true), default));

    [Fact]
    public async Task Cancelar_um_Agendado_grava_um_evento_para_todos_com_status_Cancelado()
    {
        var p = await _f.CriarAsync("Vai sair", 60, 120);

        var dto = await _f.Cancelar.Handle(new CancelarProgramaCommand(p.Id), default);

        Assert.Equal("Cancelado", dto.Status);
        var evento = Assert.Single(_f.Eventos());
        Assert.True(evento.ParaTodos);
        Assert.Equal("Cancelado", evento.Dados.GetProperty("status").GetString());
        Assert.Equal(p.Id, evento.Dados.GetProperty("programaId").GetGuid());
        Assert.Equal(_f.Locutor.Nome, evento.Dados.GetProperty("locutorNome").GetString());
    }

    [Theory]
    [InlineData("AoVivo")]
    [InlineData("Finalizado")]
    [InlineData("Cancelado")]
    public async Task So_Agendado_pode_ser_cancelado_e_o_erro_nao_grava_evento(string status)
    {
        var p = await _f.CriarAsync("X", 1, 60);
        await _f.ForcarAsync(p.Id, x =>
        {
            if (status == "AoVivo") x.IniciarAoVivo(Agora.AddMinutes(1));
            else if (status == "Finalizado") x.FinalizarPorFimDeJanela(x.FimUtc);
            else x.Cancelar(Agora);
        });

        await Assert.ThrowsAsync<TransicaoInvalidaException>(() => _f.Cancelar.Handle(new CancelarProgramaCommand(p.Id), default));
        Assert.Empty(_f.Eventos());
    }

    [Fact]
    public async Task Cancelar_libera_o_horario_para_outro_programa()
    {
        var p = await _f.CriarAsync("A", 60, 120);
        await _f.Cancelar.Handle(new CancelarProgramaCommand(p.Id), default);

        var novo = await _f.CriarAsync("B", 60, 120);

        Assert.Equal("Agendado", novo.Status);
        Assert.Equal(2, await _f.Db.Programas.CountAsync());
    }
}
