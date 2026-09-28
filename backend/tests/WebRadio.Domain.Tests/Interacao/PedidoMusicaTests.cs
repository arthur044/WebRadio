using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Interacao;
using Xunit;

namespace WebRadio.Domain.Tests.Interacao;

public class PedidoMusicaTests
{
    private static readonly DateTime Agora = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private static PedidoMusica Criar(DateTime? criadoEmUtc = null) =>
        new(
            Guid.NewGuid(),
            programaId: Guid.NewGuid(),
            usuarioId: null,
            nomeOuvinte: "Carlos",
            tituloMusica: "Faixa X",
            artista: "Artista Y",
            mensagem: null,
            listenerDeviceHash: null,
            listenerIpHash: null,
            agora: criadoEmUtc ?? Agora);

    [Fact]
    public void Construtor_comeca_Pendente()
    {
        Assert.Equal(StatusPedido.Pendente, Criar().Status);
    }

    [Fact]
    public void Aprovar_a_partir_de_Pendente_funciona()
    {
        var pedido = Criar();
        var moderador = Guid.NewGuid();
        var midiaId = Guid.NewGuid();

        pedido.Aprovar(moderador, midiaId, Agora.AddMinutes(1));

        Assert.Equal(StatusPedido.Aprovado, pedido.Status);
        Assert.Equal(midiaId, pedido.MidiaId);
        Assert.Equal(moderador, pedido.ModeradoPorUsuarioId);
    }

    [Fact]
    public void Aprovar_pedido_ja_moderado_lanca_PedidoJaModerado()
    {
        var pedido = Criar();
        pedido.Aprovar(Guid.NewGuid(), null, Agora.AddMinutes(1));

        var ex = Assert.Throws<PedidoJaModeradoException>(() => pedido.Aprovar(Guid.NewGuid(), null, Agora.AddMinutes(2)));
        Assert.Equal(pedido.Id, ex.PedidoId);
    }

    [Fact]
    public void Rejeitar_a_partir_de_Pendente_funciona()
    {
        var pedido = Criar();

        pedido.Rejeitar(Guid.NewGuid(), "conteúdo impróprio", Agora.AddMinutes(1));

        Assert.Equal(StatusPedido.Rejeitado, pedido.Status);
        Assert.Equal("conteúdo impróprio", pedido.MotivoRejeicao);
    }

    [Fact]
    public void Rejeitar_pedido_ja_moderado_lanca_PedidoJaModerado()
    {
        var pedido = Criar();
        pedido.Rejeitar(Guid.NewGuid(), "motivo", Agora.AddMinutes(1));

        Assert.Throws<PedidoJaModeradoException>(() => pedido.Rejeitar(Guid.NewGuid(), "outro motivo", Agora.AddMinutes(2)));
    }

    [Fact]
    public void MarcarTocado_a_partir_de_Aprovado_funciona()
    {
        var pedido = Criar();
        pedido.Aprovar(Guid.NewGuid(), Guid.NewGuid(), Agora.AddMinutes(1));

        pedido.MarcarTocado(Agora.AddMinutes(2));

        Assert.Equal(StatusPedido.Tocado, pedido.Status);
        Assert.NotNull(pedido.TocadoEmUtc);
    }

    [Fact]
    public void MarcarTocado_a_partir_de_Pendente_lanca_TransicaoInvalida()
    {
        var pedido = Criar();

        Assert.Throws<TransicaoInvalidaException>(() => pedido.MarcarTocado(Agora.AddMinutes(1)));
    }

    [Fact]
    public void MarcarTocado_a_partir_de_Rejeitado_lanca_TransicaoInvalida()
    {
        var pedido = Criar();
        pedido.Rejeitar(Guid.NewGuid(), "motivo", Agora.AddMinutes(1));

        Assert.Throws<TransicaoInvalidaException>(() => pedido.MarcarTocado(Agora.AddMinutes(2)));
    }

    [Fact]
    public void MarcarTocado_a_partir_de_Tocado_lanca_TransicaoInvalida()
    {
        var pedido = Criar();
        pedido.Aprovar(Guid.NewGuid(), Guid.NewGuid(), Agora.AddMinutes(1));
        pedido.MarcarTocado(Agora.AddMinutes(2));

        Assert.Throws<TransicaoInvalidaException>(() => pedido.MarcarTocado(Agora.AddMinutes(3)));
    }

    [Fact]
    public void MarcarTocado_a_partir_de_Expirado_lanca_TransicaoInvalida()
    {
        var pedido = Criar(criadoEmUtc: Agora);
        pedido.Expirar(TimeSpan.FromMinutes(120), Agora.AddMinutes(121));

        Assert.Throws<TransicaoInvalidaException>(() => pedido.MarcarTocado(Agora.AddMinutes(122)));
    }

    [Fact]
    public void MarcarEnviadoAoPlayout_exige_Aprovado()
    {
        var pedido = Criar();

        Assert.Throws<TransicaoInvalidaException>(() => pedido.MarcarEnviadoAoPlayout(Agora.AddMinutes(1)));

        pedido.Aprovar(Guid.NewGuid(), Guid.NewGuid(), Agora.AddMinutes(1));
        pedido.MarcarEnviadoAoPlayout(Agora.AddMinutes(2));

        Assert.NotNull(pedido.EnviadoPlayoutEmUtc);
    }

    [Fact]
    public void Expirar_apos_o_prazo_funciona()
    {
        var pedido = Criar(criadoEmUtc: Agora);

        pedido.Expirar(TimeSpan.FromMinutes(120), Agora.AddMinutes(121));

        Assert.Equal(StatusPedido.Expirado, pedido.Status);
    }

    [Fact]
    public void Expirar_antes_do_prazo_lanca_TransicaoInvalida()
    {
        var pedido = Criar(criadoEmUtc: Agora);

        Assert.Throws<TransicaoInvalidaException>(() => pedido.Expirar(TimeSpan.FromMinutes(120), Agora.AddMinutes(60)));
    }

    [Fact]
    public void Expirar_pedido_ja_aprovado_lanca_PedidoJaModerado()
    {
        var pedido = Criar(criadoEmUtc: Agora);
        pedido.Aprovar(Guid.NewGuid(), null, Agora.AddMinutes(1));

        Assert.Throws<PedidoJaModeradoException>(() => pedido.Expirar(TimeSpan.FromMinutes(120), Agora.AddMinutes(200)));
    }
}
