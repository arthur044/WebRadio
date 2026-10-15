using FluentValidation;
using WebRadio.Application.Features.Programas;
using WebRadio.Domain.Erros;
using Xunit;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Programas;

/// <summary>E2-F01: consultas de grade (intervalo de até 31 dias), programa ao vivo e por id.</summary>
public class ConsultasDeProgramaTests
{
    private readonly ProgramasFixture _f = new();

    [Fact]
    public async Task Lista_o_intervalo_ordenado_por_inicio_e_inclui_o_que_cruza_as_bordas()
    {
        await _f.CriarAsync("C", 300, 360);
        await _f.CriarAsync("A", 60, 120);
        await _f.CriarAsync("B", 130, 190);
        await _f.CriarAsync("Fora", 24 * 60 * 3, 24 * 60 * 3 + 60);

        // Janela [120, 320): "A" termina em 120 (encostado: fora), "B" entra inteiro, "C" começa dentro e termina fora (cruza a borda).
        var lista = await _f.Listar.Handle(new ListarProgramasQuery(Agora.AddMinutes(120), Agora.AddMinutes(320), false), default);

        Assert.Equal(["B", "C"], lista.Select(p => p.Titulo));
    }

    [Fact]
    public async Task Programa_que_comecou_antes_do_intervalo_e_termina_dentro_aparece()
    {
        await _f.CriarAsync("Longo", 1, 600);

        var lista = await _f.Listar.Handle(new ListarProgramasQuery(Agora.AddMinutes(300), Agora.AddMinutes(400), false), default);

        Assert.Equal("Longo", Assert.Single(lista).Titulo);
    }

    [Fact]
    public async Task Cancelados_so_aparecem_quando_pedido()
    {
        await _f.CriarAsync("Fica", 60, 120);
        var c = await _f.CriarAsync("Sai", 200, 260);
        await _f.Cancelar.Handle(new CancelarProgramaCommand(c.Id), default);
        var q = (bool incluir) => new ListarProgramasQuery(Agora, Agora.AddDays(1), incluir);

        Assert.Equal(["Fica"], (await _f.Listar.Handle(q(false), default)).Select(p => p.Titulo));
        Assert.Equal(["Fica", "Sai"], (await _f.Listar.Handle(q(true), default)).Select(p => p.Titulo));
    }

    [Theory]
    [InlineData(31, true)]
    [InlineData(32, false)]
    public void Intervalo_maximo_e_de_31_dias(int dias, bool valido)
        => Assert.Equal(valido, new ListarProgramasValidator().Validate(new ListarProgramasQuery(Agora, Agora.AddDays(dias), false)).IsValid);

    [Fact]
    public void Intervalo_invertido_vazio_ou_fora_de_UTC_e_recusado()
    {
        var v = new ListarProgramasValidator();
        Assert.False(v.Validate(new ListarProgramasQuery(Agora.AddDays(1), Agora, false)).IsValid);
        Assert.False(v.Validate(new ListarProgramasQuery(Agora, Agora, false)).IsValid);
        Assert.False(v.Validate(new ListarProgramasQuery(DateTime.SpecifyKind(Agora, DateTimeKind.Unspecified), Agora.AddDays(1), false)).IsValid);
    }

    [Fact]
    public async Task Ao_vivo_devolve_o_programa_AoVivo_ou_nulo()
    {
        Assert.Null(await _f.AoVivo.Handle(new ProgramaAoVivoQuery(), default));
        var p = await _f.CriarAsync("No ar", 0, 60);
        Assert.Null(await _f.AoVivo.Handle(new ProgramaAoVivoQuery(), default)); // Agendado ainda não é ao vivo (quem muda é o Worker)

        await _f.ForcarAsync(p.Id, x => x.IniciarAoVivo(Agora.AddSeconds(1)));

        var vivo = await _f.AoVivo.Handle(new ProgramaAoVivoQuery(), default);
        Assert.Equal(p.Id, vivo!.Id);
        Assert.Equal("AoVivo", vivo.Status);
    }

    [Fact]
    public async Task Por_id_devolve_o_programa_com_versao_e_404_quando_nao_existe()
    {
        var p = await _f.CriarAsync("A", 60, 120);

        var dto = await _f.PorId.Handle(new ProgramaPorIdQuery(p.Id), default);

        Assert.Equal(p.Id, dto.Id);
        Assert.Equal(p.Versao, dto.Versao);
        await Assert.ThrowsAsync<NaoEncontradoException>(() => _f.PorId.Handle(new ProgramaPorIdQuery(Guid.NewGuid()), default));
    }
}
