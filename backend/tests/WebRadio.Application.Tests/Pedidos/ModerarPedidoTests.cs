using FluentValidation;
using WebRadio.Application.Features.Pedidos;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using Xunit;

namespace WebRadio.Application.Tests.Pedidos;

/// <summary>07 E3-F03: aprovar, rejeitar e marcar tocado, com o outbox para o ouvinte e para os locutores.</summary>
public class ModerarPedidoTests
{
    private readonly PedidosFixture _f = new();
    private readonly Guid _moderador = Guid.NewGuid();

    [Fact]
    public async Task Aprovar_muda_o_status_registra_o_moderador_e_avisa_ouvinte_e_locutores()
    {
        var ouvinte = Guid.NewGuid();
        var pedido = await _f.InserirAsync(ouvinte);

        var dto = await _f.Aprovar.Handle(new AprovarPedidoCommand(pedido.Id, _moderador, null), default);

        Assert.Equal("Aprovado", dto.Status);
        Assert.Equal(_moderador, pedido.ModeradoPorUsuarioId);
        var (tipo, dados, grupos) = Assert.Single(_f.Eventos());
        Assert.Equal("PedidoModerado", tipo);
        Assert.Equal(["locutores", GruposDoHubDe(ouvinte)], grupos!);
        Assert.Equal(pedido.Id, dados.GetProperty("pedidoId").GetGuid());
        Assert.Equal("Aprovado", dados.GetProperty("status").GetString());
    }

    private string GruposDoHubDe(Guid listenerId) => _f.Identidade.GrupoListener(listenerId);

    [Fact]
    public async Task Pedido_anonimizado_sem_hash_ainda_pode_ser_moderado_e_so_vai_para_locutores()
    {
        var pedido = await _f.InserirAsync(semHash: true);

        await _f.Aprovar.Handle(new AprovarPedidoCommand(pedido.Id, _moderador, null), default);

        Assert.Equal(["locutores"], Assert.Single(_f.Eventos()).Grupos!);
    }

    [Fact]
    public async Task Aprovar_com_midiaId_responde_400_porque_nenhuma_midia_Aprovado_existe_no_Epico_3()
    {
        var pedido = await _f.InserirAsync();

        var erro = await Assert.ThrowsAsync<ValidationException>(() => _f.Aprovar.Handle(new AprovarPedidoCommand(pedido.Id, _moderador, Guid.NewGuid()), default));

        Assert.Equal("MidiaId", Assert.Single(erro.Errors).PropertyName);
        Assert.Equal(StatusPedido.Pendente, pedido.Status);
        Assert.Empty(_f.Eventos());
    }

    [Fact]
    public async Task Aprovar_pedido_inexistente_e_404()
    {
        await Assert.ThrowsAsync<NaoEncontradoException>(() => _f.Aprovar.Handle(new AprovarPedidoCommand(Guid.NewGuid(), _moderador, null), default));
    }

    [Fact]
    public async Task Aprovar_ou_rejeitar_pedido_ja_moderado_e_PedidoJaModerado()
    {
        var pedido = await _f.InserirAsync();
        await _f.Aprovar.Handle(new AprovarPedidoCommand(pedido.Id, _moderador, null), default);

        await Assert.ThrowsAsync<PedidoJaModeradoException>(() => _f.Aprovar.Handle(new AprovarPedidoCommand(pedido.Id, _moderador, null), default));
        await Assert.ThrowsAsync<PedidoJaModeradoException>(() => _f.Rejeitar.Handle(new RejeitarPedidoCommand(pedido.Id, _moderador, "não"), default));
        Assert.Single(_f.Eventos()); // só o primeiro evento
    }

    [Fact]
    public async Task Rejeitar_grava_o_motivo_normalizado_e_manda_para_o_ouvinte()
    {
        var ouvinte = Guid.NewGuid();
        var pedido = await _f.InserirAsync(ouvinte);

        var dto = await _f.Rejeitar.Handle(new RejeitarPedidoCommand(pedido.Id, _moderador, "  Fora do estilo da casa  "), default);

        Assert.Equal("Rejeitado", dto.Status);
        Assert.Equal("Fora do estilo da casa", dto.MotivoRejeicao);
        var (_, dados, grupos) = Assert.Single(_f.Eventos());
        Assert.Equal("Fora do estilo da casa", dados.GetProperty("motivoRejeicao").GetString());
        Assert.Contains(GruposDoHubDe(ouvinte), grupos!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("motivo\ncom quebra")]
    [InlineData("motivo‮bidi")]
    public void Motivo_invalido_e_400(string motivo) =>
        Assert.False(new RejeitarPedidoValidator().Validate(new RejeitarPedidoCommand(Guid.NewGuid(), _moderador, motivo)).IsValid);

    [Fact]
    public void Motivo_vai_de_1_a_280_unidades_UTF16()
    {
        var v = new RejeitarPedidoValidator();
        Assert.True(v.Validate(new RejeitarPedidoCommand(Guid.NewGuid(), _moderador, "a")).IsValid);
        Assert.True(v.Validate(new RejeitarPedidoCommand(Guid.NewGuid(), _moderador, new string('a', 280))).IsValid);
        Assert.False(v.Validate(new RejeitarPedidoCommand(Guid.NewGuid(), _moderador, new string('a', 281))).IsValid);
    }

    [Fact]
    public async Task Marcar_tocado_so_vale_para_Aprovado_e_os_outros_status_sao_PedidoJaModerado()
    {
        var pendente = await _f.InserirAsync();
        await Assert.ThrowsAsync<PedidoJaModeradoException>(() => _f.Tocar.Handle(new MarcarPedidoTocadoCommand(pendente.Id), default));

        var aprovado = await _f.InserirAsync(status: StatusPedido.Aprovado);
        var dto = await _f.Tocar.Handle(new MarcarPedidoTocadoCommand(aprovado.Id), default);

        Assert.Equal("Tocado", dto.Status);
        Assert.NotNull(dto.TocadoEmUtc);
        var (tipo, dados, _) = Assert.Single(_f.Eventos());
        Assert.Equal("PedidoTocado", tipo);
        Assert.Equal(aprovado.Id, dados.GetProperty("pedidoId").GetGuid());

        await Assert.ThrowsAsync<PedidoJaModeradoException>(() => _f.Tocar.Handle(new MarcarPedidoTocadoCommand(aprovado.Id), default));
    }
}
