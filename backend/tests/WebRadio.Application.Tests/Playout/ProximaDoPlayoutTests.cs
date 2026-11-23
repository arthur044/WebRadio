using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Playout;
using WebRadio.Application.Tests.Midias;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Midia;
using Xunit;

namespace WebRadio.Application.Tests.Playout;

internal sealed class FilaFalsa : IFilaDoPlayout
{
    public Queue<PedidoParaTocar?> Pedidos { get; } = new();

    public Dictionary<TipoMidia, CandidataDaRotacao?> ForaDaJanela { get; } = [];

    /// <summary>Várias candidatas por tipo (a rotação sorteia UMA entre elas, K = 5); tem precedência sobre <see cref="ForaDaJanela"/>.</summary>
    public Dictionary<TipoMidia, List<CandidataDaRotacao>> VariasForaDaJanela { get; } = [];

    public Dictionary<TipoMidia, CandidataDaRotacao?> Antigas { get; } = [];

    public List<(TipoMidia Tipo, int R, DateTime Corte, Guid EmVoo)> BuscasForaDaJanela { get; } = [];

    public List<(TipoMidia Tipo, DateTime Corte, Guid EmVoo)> BuscasAntigas { get; } = [];

    public List<(Guid Instancia, DateTime Agora)> Capturas { get; } = [];

    public Task<PedidoParaTocar?> CapturarPedidoAsync(Guid instancia, DateTime agora, CancellationToken ct)
    {
        Capturas.Add((instancia, agora));
        return Task.FromResult(Pedidos.Count > 0 ? Pedidos.Dequeue() : null);
    }

    public Task<IReadOnlyList<CandidataDaRotacao>> CandidatasForaDaJanelaAsync(TipoMidia tipo, int sorteio, DateTime corteDaJanela, Guid emVoo, CancellationToken ct)
    {
        BuscasForaDaJanela.Add((tipo, sorteio, corteDaJanela, emVoo));
        if (VariasForaDaJanela.TryGetValue(tipo, out var varias)) return Task.FromResult<IReadOnlyList<CandidataDaRotacao>>(varias);
        return Task.FromResult<IReadOnlyList<CandidataDaRotacao>>(ForaDaJanela.GetValueOrDefault(tipo) is { } uma ? [uma] : []);
    }

    public Task<CandidataDaRotacao?> MaisAntigaDaJanelaAsync(TipoMidia tipo, DateTime corteDaJanela, Guid emVoo, CancellationToken ct)
    {
        BuscasAntigas.Add((tipo, corteDaJanela, emVoo));
        return Task.FromResult(Antigas.GetValueOrDefault(tipo));
    }
}

internal sealed class EmVooFalso : IFaixaEmVoo
{
    public Guid Atual { get; set; } = Guid.Empty;

    public Task DefinirAsync(Guid midiaId, CancellationToken ct)
    {
        Atual = midiaId;
        return Task.CompletedTask;
    }

    public Task<Guid> ObterAsync(CancellationToken ct) => Task.FromResult(Atual);
}

internal sealed class AleatorioFalso(params int[] sorteios) : IAleatorio
{
    private readonly Queue<int> _fila = new(sorteios);

    public int Chave { get; set; } = 12345;

    public int Proximo(int minimo, int maximoExclusivo) => _fila.Count > 0 ? _fila.Dequeue() : minimo;

    public int ChaveDoSorteio() => Chave;
}

/// <summary>E4-F08 (E4-D4, E4-D5): a <c>proxima</c> do playout com a fila SQL, a faixa em voo e o sorteio de mentira (o SQL real tem teste com SQL Server).</summary>
public sealed class ProximaDoPlayoutTests
{
    private static readonly Guid Instancia = Guid.NewGuid();

    private sealed class Cena
    {
        public MidiasFixture F { get; } = new();
        public FilaFalsa Fila { get; } = new();
        public EmVooFalso EmVoo { get; } = new();
        public RotacaoOptions Rotacao { get; } = new();

        public ProximaDoPlayoutHandler Handler(params int[] sorteios) => new(Fila, F.Db, F.Relogio, Options.Create(Rotacao), EmVoo, new AleatorioFalso(sorteios), F.Storage);

        public ArquivoMidia Aprovada(TipoMidia tipo = TipoMidia.Musica, string titulo = "Aurora", bool ativa = true) => F.SemearAprovada(titulo, "Banda", tipo, ativo: ativa);
    }

    [Fact]
    public async Task Pedido_aprovado_vem_primeiro_com_pedidoId_e_a_url_interna_de_30_min_e_vira_a_faixa_em_voo()
    {
        var c = new Cena();
        var m = c.Aprovada();
        var pedido = Guid.NewGuid();
        c.Fila.Pedidos.Enqueue(new PedidoParaTocar(pedido, m.Id));

        var r = await c.Handler().Handle(new ProximaDoPlayoutQuery(Instancia), default);

        Assert.NotNull(r);
        Assert.Equal((m.Id, (Guid?)pedido, "Aurora", "Banda"), (r.MidiaId, r.PedidoId, r.Titulo, r.Artista));
        Assert.StartsWith("http://minio:9000/midia/", r.Url);
        Assert.Equal(("midia", m.ChaveStorage, TimeSpan.FromMinutes(30)), c.F.Storage.Internas.Single());
        Assert.Equal(m.Id, c.EmVoo.Atual);
        Assert.Empty(c.Fila.BuscasForaDaJanela); // a rotação nem foi consultada
        Assert.Equal(Instancia, c.Fila.Capturas.Single().Instancia);
    }

    [Fact]
    public async Task Sem_pedido_a_rotacao_devolve_faixa_com_pedidoId_nulo_e_titulo_do_indice()
    {
        var c = new Cena();
        var m = c.Aprovada(TipoMidia.Musica, "Do banco");
        c.Fila.ForaDaJanela[TipoMidia.Musica] = new CandidataDaRotacao(m.Id, "Do índice", "Artista do índice");

        var r = await c.Handler(0).Handle(new ProximaDoPlayoutQuery(Instancia), default); // sorteado 0 de 11 → Musica

        Assert.Equal((m.Id, (Guid?)null, "Do índice", "Artista do índice"), (r!.MidiaId, r.PedidoId, r.Titulo, r.Artista));
        Assert.Equal(m.Id, c.EmVoo.Atual);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    public async Task Com_varias_candidatas_a_aplicacao_sorteia_UMA_entre_elas_pelo_IAleatorio(int indiceSorteado, int esperado)
    {
        var c = new Cena();
        var cinco = Enumerable.Range(0, 5).Select(i => c.Aprovada(TipoMidia.Musica, "Faixa" + i)).ToList();
        c.Fila.VariasForaDaJanela[TipoMidia.Musica] = [.. cinco.Select(m => new CandidataDaRotacao(m.Id, m.Titulo, "Banda"))];

        // 1º sorteio: o tipo (0 de 11 = Musica); 2º: o índice entre as candidatas (E4-A01b, K = 5)
        var r = await c.Handler(0, indiceSorteado).Handle(new ProximaDoPlayoutQuery(Instancia), default);

        Assert.Equal(cinco[esperado].Id, r!.MidiaId);
        Assert.Equal(cinco[esperado].Id, c.EmVoo.Atual);
    }

    [Fact]
    public async Task Com_uma_so_candidata_nao_ha_sorteio_e_sem_nenhuma_cai_na_busca_3()
    {
        var c = new Cena();
        var unica = c.Aprovada(TipoMidia.Musica, "Única");
        c.Fila.VariasForaDaJanela[TipoMidia.Musica] = [new CandidataDaRotacao(unica.Id, "Única", "Banda")];
        Assert.Equal(unica.Id, (await c.Handler(0, 4).Handle(new ProximaDoPlayoutQuery(Instancia), default))!.MidiaId); // o 4 sobraria: com uma candidata o índice não é sorteado

        var d = new Cena();
        var antiga = d.Aprovada(TipoMidia.Musica, "Antiga");
        d.Fila.VariasForaDaJanela[TipoMidia.Musica] = [];
        d.Fila.Antigas[TipoMidia.Musica] = new CandidataDaRotacao(antiga.Id, "Antiga", "Banda");
        Assert.Equal(antiga.Id, (await d.Handler(0).Handle(new ProximaDoPlayoutQuery(Instancia), default))!.MidiaId);
    }

    [Theory]
    [InlineData(0, TipoMidia.Musica)]
    [InlineData(7, TipoMidia.Musica)]
    [InlineData(8, TipoMidia.Vinheta)]
    [InlineData(9, TipoMidia.Vinheta)]
    [InlineData(10, TipoMidia.Comercial)]
    public async Task O_tipo_e_escolhido_pelos_pesos_8_2_1(int sorteado, TipoMidia esperado)
    {
        var c = new Cena();
        foreach (var t in new[] { TipoMidia.Musica, TipoMidia.Vinheta, TipoMidia.Comercial }) c.Fila.ForaDaJanela[t] = new CandidataDaRotacao(c.Aprovada(t, "x" + t).Id, null, null);

        await c.Handler(sorteado).Handle(new ProximaDoPlayoutQuery(Instancia), default);

        Assert.Equal(esperado, c.Fila.BuscasForaDaJanela.First().Tipo);
    }

    [Fact]
    public async Task Tipo_sem_candidata_sai_do_sorteio_e_o_proximo_tipo_e_tentado()
    {
        var c = new Cena();
        var v = c.Aprovada(TipoMidia.Vinheta, "Vinheta");
        c.Fila.ForaDaJanela[TipoMidia.Vinheta] = new CandidataDaRotacao(v.Id, "Vinheta", null);

        // 1º sorteio: Musica (sem candidata, nem a busca 3); sai do sorteio. Restam Vinheta 2 e Comercial 1: sorteado 0 → Vinheta.
        var r = await c.Handler(0, 0).Handle(new ProximaDoPlayoutQuery(Instancia), default);

        Assert.Equal(v.Id, r!.MidiaId);
        Assert.Equal([TipoMidia.Musica, TipoMidia.Vinheta], c.Fila.BuscasForaDaJanela.Select(b => b.Tipo).ToArray());
        Assert.Equal(TipoMidia.Musica, c.Fila.BuscasAntigas.Single().Tipo); // a busca 3 também foi tentada antes de tirar o tipo
    }

    [Fact]
    public async Task Sem_candidata_em_nenhum_tipo_e_204()
    {
        var c = new Cena();
        Assert.Null(await c.Handler(0, 0, 0).Handle(new ProximaDoPlayoutQuery(Instancia), default));
        Assert.Equal(3, c.Fila.BuscasForaDaJanela.Count);
        Assert.Equal(Guid.Empty, c.EmVoo.Atual);
    }

    [Fact]
    public async Task A_busca_3_so_roda_quando_as_buscas_1_e_2_nao_acham_e_a_faixa_em_voo_e_excluida_em_todas()
    {
        var c = new Cena();
        var voo = Guid.NewGuid();
        c.EmVoo.Atual = voo;
        var m = c.Aprovada();
        c.Fila.Antigas[TipoMidia.Musica] = new CandidataDaRotacao(m.Id, "Antiga", null);

        var r = await c.Handler(0).Handle(new ProximaDoPlayoutQuery(Instancia), default);

        Assert.Equal(m.Id, r!.MidiaId);
        Assert.Equal(voo, c.Fila.BuscasForaDaJanela.Single().EmVoo);
        Assert.Equal(voo, c.Fila.BuscasAntigas.Single().EmVoo);
        Assert.Equal(m.Id, c.EmVoo.Atual); // a nova é a em voo
    }

    [Fact]
    public async Task A_janela_de_repeticao_e_de_90_min_para_musica_e_20_para_os_outros_e_a_chave_do_sorteio_vai_a_busca()
    {
        var c = new Cena();
        await c.Handler(0, 0, 0).Handle(new ProximaDoPlayoutQuery(Instancia), default);

        var agora = c.F.Relogio.UtcNow;
        var porTipo = c.Fila.BuscasForaDaJanela.ToDictionary(b => b.Tipo);
        Assert.Equal(agora.AddMinutes(-90), porTipo[TipoMidia.Musica].Corte);
        Assert.Equal(agora.AddMinutes(-20), porTipo[TipoMidia.Vinheta].Corte);
        Assert.Equal(agora.AddMinutes(-20), porTipo[TipoMidia.Comercial].Corte);
        Assert.All(porTipo.Values, b => Assert.Equal(12345, b.R));
    }

    [Fact]
    public async Task Peso_zero_tira_o_tipo_da_rotacao()
    {
        var c = new Cena();
        c.Rotacao.Pesos.Comercial = 0;
        c.Rotacao.Pesos.Vinheta = 0;

        await c.Handler(0, 0).Handle(new ProximaDoPlayoutQuery(Instancia), default);

        Assert.Equal([TipoMidia.Musica], c.Fila.BuscasForaDaJanela.Select(b => b.Tipo).ToArray());
    }

    [Fact]
    public async Task Pedido_cuja_midia_sumiu_cai_na_rotacao_e_midia_desativada_nunca_e_devolvida()
    {
        var c = new Cena();
        var desativada = c.Aprovada(TipoMidia.Musica, "Desativada", ativa: false);
        c.Fila.Pedidos.Enqueue(new PedidoParaTocar(Guid.NewGuid(), desativada.Id));
        c.Fila.ForaDaJanela[TipoMidia.Musica] = new CandidataDaRotacao(desativada.Id, "x", null); // uma fila inconsistente: a conferência pela PK também barra

        var r = await c.Handler(0, 0, 0).Handle(new ProximaDoPlayoutQuery(Instancia), default);

        Assert.Null(r);
        Assert.Empty(c.F.Storage.Internas);
    }

    [Fact]
    public void O_ToString_do_dto_nao_imprime_a_url_assinada()
        => Assert.DoesNotContain("Signature", new ProximaFaixaDto(Guid.NewGuid(), null, "http://minio:9000/midia/x?X-Amz-Signature=abc", "t", "a").ToString());

    [Fact]
    public void As_opcoes_invalidas_falham_o_boot()
    {
        var v = new RotacaoOptionsValidator();
        Assert.True(v.Validate(null, new RotacaoOptions()).Succeeded);
        Assert.True(v.Validate(null, new RotacaoOptions { Pesos = new PesosDaRotacao { Musica = 0, Vinheta = 0, Comercial = 0 } }).Failed);
        Assert.True(v.Validate(null, new RotacaoOptions { Pesos = new PesosDaRotacao { Musica = -1 } }).Failed);
        Assert.True(v.Validate(null, new RotacaoOptions { JanelaMinutos = new JanelaDaRotacao { Musica = 20_000 } }).Failed);
    }
}
