using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Playout;
using WebRadio.Application.Tests.Midias;
using WebRadio.Application.Tests.Pedidos;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Interacao;
using WebRadio.Domain.Midia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using Xunit;

namespace WebRadio.Application.Tests.Playout;

internal sealed class TocandoAgoraFalso : ITocandoAgoraStore
{
    public List<TocandoAgora> Definidos { get; } = [];

    public Task DefinirAsync(TocandoAgora faixa, CancellationToken ct)
    {
        Definidos.Add(faixa);
        return Task.CompletedTask;
    }

    public Task<TocandoAgora?> ObterAsync(CancellationToken ct) => Task.FromResult(Definidos.Count == 0 ? null : Definidos[^1]);
}

/// <summary>E4-F08: <c>FaixaIniciada</c> grava a <c>Reproducao</c>, marca o pedido <c>Tocado</c> e publica os eventos, sem duplicar quando o Liquidsoap reenvia.</summary>
public sealed class FaixaIniciadaTests
{
    private sealed class Cena
    {
        public MidiasFixture F { get; } = new();
        public TocandoAgoraFalso Tocando { get; } = new();
        public ConsultasDePedidosFalsas Consultas { get; } = new() { Programa = Guid.NewGuid() };

        public RegistrarEventoDoPlayoutHandler Handler => new(Tocando, new OutboxEf(F.Db, F.Relogio), F.Db, Consultas, F.Relogio);

        public RegistrarEventoDoPlayoutCommand Comando(Guid? midiaId, Guid? pedidoId = null, string? titulo = "Aurora") =>
            new("FaixaIniciada", midiaId, pedidoId, titulo, "Banda", F.Relogio.UtcNow);

        public PedidoMusica PedidoAprovado(Guid midiaId)
        {
            var p = new PedidoMusica(Guid.NewGuid(), null, null, "Ana", "Aurora", "Banda", null, null, null, F.Relogio.UtcNow);
            p.Aprovar(Guid.NewGuid(), midiaId, F.Relogio.UtcNow);
            F.Db.PedidosMusica.Add(p);
            F.Db.SaveChanges();
            return p;
        }

        public int Eventos(string tipo) => F.Db.EventosOutbox.Count(e => e.Tipo == tipo);
    }

    [Fact]
    public async Task Faixa_de_pedido_grava_a_reproducao_com_o_programa_da_janela_marca_Tocado_e_publica_os_dois_eventos()
    {
        var c = new Cena();
        var m = c.F.SemearAprovada();
        var p = c.PedidoAprovado(m.Id);

        await c.Handler.Handle(c.Comando(m.Id, p.Id), default);

        var r = c.F.Db.Reproducoes.Single();
        Assert.Equal((m.Id, (Guid?)p.Id, c.Consultas.Programa), (r.MidiaId, r.PedidoId, r.ProgramaId));
        Assert.Equal(StatusPedido.Tocado, c.F.Db.PedidosMusica.Single().Status);
        Assert.Equal(1, c.Eventos("PedidoTocado"));
        Assert.Equal(1, c.Eventos("TocandoAgora"));
        Assert.Contains("\"tipoMidia\":\"Musica\"", c.F.Db.EventosOutbox.Single(e => e.Tipo == "TocandoAgora").Payload);
        Assert.Equal(p.Id, c.Tocando.Definidos.Single().PedidoId);
    }

    [Fact]
    public async Task FaixaIniciada_repetido_nao_duplica_a_reproducao_nem_o_Tocado_nem_os_eventos()
    {
        var c = new Cena();
        var m = c.F.SemearAprovada();
        var p = c.PedidoAprovado(m.Id);

        await c.Handler.Handle(c.Comando(m.Id, p.Id), default);
        await c.Handler.Handle(c.Comando(m.Id, p.Id), default);

        Assert.Single(c.F.Db.Reproducoes);
        Assert.Equal(1, c.Eventos("PedidoTocado"));
        Assert.Equal(1, c.Eventos("TocandoAgora"));
        Assert.Single(c.Tocando.Definidos);
    }

    [Fact]
    public async Task Faixa_da_rotacao_grava_a_reproducao_sem_pedido_e_publica_so_o_TocandoAgora_com_o_tipo()
    {
        var c = new Cena();
        var v = c.F.SemearAprovada("Vinheta", null, TipoMidia.Vinheta, nome: "v.mp3");

        await c.Handler.Handle(c.Comando(v.Id, titulo: "Vinheta"), default);

        var r = c.F.Db.Reproducoes.Single();
        Assert.Equal((v.Id, (Guid?)null), (r.MidiaId, r.PedidoId));
        Assert.Equal(0, c.Eventos("PedidoTocado"));
        Assert.Contains("\"tipoMidia\":\"Vinheta\"", c.F.Db.EventosOutbox.Single(e => e.Tipo == "TocandoAgora").Payload);
    }

    [Fact]
    public async Task Sem_midiaId_e_rotacao_local_ou_ao_vivo_so_atualiza_o_tocando_agora_com_tipo_nulo()
    {
        var c = new Cena();
        await c.Handler.Handle(c.Comando(null), default);

        Assert.Empty(c.F.Db.Reproducoes);
        Assert.Contains("\"tipoMidia\":null", c.F.Db.EventosOutbox.Single().Payload);
        Assert.Single(c.Tocando.Definidos);
    }

    [Fact]
    public async Task Pedido_desconhecido_ou_midia_excluida_nao_quebra_o_evento()
    {
        var c = new Cena();
        var m = c.F.SemearAprovada();
        await c.Handler.Handle(c.Comando(m.Id, Guid.NewGuid()), default); // pedido que não existe: a reprodução fica sem PedidoId
        Assert.Null(c.F.Db.Reproducoes.Single().PedidoId);

        await c.Handler.Handle(c.Comando(Guid.NewGuid()), default); // mídia que não existe (excluída depois de entregue): só o tocando agora
        Assert.Single(c.F.Db.Reproducoes);
        Assert.Equal(2, c.Tocando.Definidos.Count);
    }

    [Fact]
    public async Task Pedido_ja_marcado_Tocado_pelo_locutor_ainda_ganha_a_reproducao_mas_nao_um_segundo_PedidoTocado()
    {
        var c = new Cena();
        var m = c.F.SemearAprovada();
        var p = c.PedidoAprovado(m.Id);
        c.F.Db.PedidosMusica.Single().MarcarTocado(c.F.Relogio.UtcNow); // o locutor marcou à mão antes
        c.F.Db.SaveChanges();

        await c.Handler.Handle(c.Comando(m.Id, p.Id), default);

        Assert.Single(c.F.Db.Reproducoes);
        Assert.Equal(0, c.Eventos("PedidoTocado"));
    }

    /// <summary>Um IRadioDbContext que delega ao real e faz a N-ésima chamada de SaveChanges falhar por concorrência (o RowVersion do pedido perdido para um MarcarTocado manual).</summary>
    private sealed class DbQuePerdeOTocado(WebRadio.Application.Abstracoes.IRadioDbContext real, int chamadaQueFalha) : WebRadio.Application.Abstracoes.IRadioDbContext
    {
        private int _chamadas;

        public Microsoft.EntityFrameworkCore.DbSet<WebRadio.Domain.Usuarios.Usuario> Usuarios => real.Usuarios;
        public Microsoft.EntityFrameworkCore.DbSet<WebRadio.Domain.Autenticacao.RefreshToken> RefreshTokens => real.RefreshTokens;
        public Microsoft.EntityFrameworkCore.DbSet<WebRadio.Domain.Transmissao.CredencialTransmissao> CredenciaisTransmissao => real.CredenciaisTransmissao;
        public Microsoft.EntityFrameworkCore.DbSet<WebRadio.Domain.Grade.Programa> Programas => real.Programas;
        public Microsoft.EntityFrameworkCore.DbSet<PedidoMusica> PedidosMusica => real.PedidosMusica;
        public Microsoft.EntityFrameworkCore.DbSet<ArquivoMidia> ArquivosMidia => real.ArquivosMidia;
        public Microsoft.EntityFrameworkCore.DbSet<Divulgacao> Divulgacoes => real.Divulgacoes;
        public Microsoft.EntityFrameworkCore.DbSet<Reproducao> Reproducoes => real.Reproducoes;

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            ++_chamadas == chamadaQueFalha ? throw new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("RowVersion") : real.SaveChangesAsync(cancellationToken);
    }

    [Fact]
    public async Task Locutor_vence_o_RowVersion_do_pedido_a_faixa_que_tocou_ainda_fica_na_reproducao_e_no_tocando_agora_sem_um_segundo_PedidoTocado()
    {
        var c = new Cena();
        var m = c.F.SemearAprovada();
        var p = c.PedidoAprovado(m.Id);
        var handler = new RegistrarEventoDoPlayoutHandler(c.Tocando, new OutboxEf(c.F.Db, c.F.Relogio), new DbQuePerdeOTocado(c.F.Db, chamadaQueFalha: 2), c.Consultas, c.F.Relogio);

        await handler.Handle(c.Comando(m.Id, p.Id), default); // não lança: a perda da corrida é esperada

        Assert.Single(c.F.Db.Reproducoes);
        Assert.Equal(1, c.Eventos("TocandoAgora"));
        Assert.Equal(0, c.Eventos("PedidoTocado")); // o do locutor já saiu do outro lado
        Assert.Single(c.Tocando.Definidos);
    }

    [Fact]
    public async Task Evento_com_a_midia_diferente_da_do_pedido_e_tratado_como_rotacao_e_nao_marca_o_pedido()
    {
        var c = new Cena();
        var doPedido = c.F.SemearAprovada();
        var outra = c.F.SemearAprovada("Outra", null, TipoMidia.Musica, nome: "outra.mp3");
        var p = c.PedidoAprovado(doPedido.Id);

        await c.Handler.Handle(c.Comando(outra.Id, p.Id), default);

        Assert.Equal(StatusPedido.Aprovado, c.F.Db.PedidosMusica.Single().Status);
        Assert.Equal(0, c.Eventos("PedidoTocado"));
        var r = c.F.Db.Reproducoes.Single();
        Assert.Equal((outra.Id, (Guid?)null), (r.MidiaId, r.PedidoId));
        Assert.Null(c.Tocando.Definidos.Single().PedidoId);
    }
}
