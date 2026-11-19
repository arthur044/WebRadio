using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Interacao;
using WebRadio.Domain.Midia;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Playout;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// E4-F08 contra SQL Server de verdade: o SQL do Atlas (<c>docs/pareceres/e4-a01a-consultas-playout.md</c>) com os hints <c>INDEX(...)</c>, o <c>UPDLOCK, READPAST</c> e a coluna calculada <c>Sorteio</c>.
/// </summary>
public class PlayoutSqlTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private readonly Cenario _c = new(banco);

    /// <summary>O banco é da classe: cada teste começa sem pedido, reprodução nem mídia dos outros.</summary>
    private async Task ZerarAsync()
    {
        await using var db = _c.NovoDb();
        await db.Reproducoes.ExecuteDeleteAsync();
        await db.PedidosMusica.ExecuteDeleteAsync();
        await db.ArquivosMidia.ExecuteDeleteAsync();
    }

    private async Task<Guid> DonoAsync() => (await _c.CriarUsuarioAsync(Cenario.Email(), Role.Locutor)).Id;

    private async Task<ArquivoMidia> MidiaAsync(Guid dono, TipoMidia tipo = TipoMidia.Musica, bool ativa = true, string titulo = "Faixa")
    {
        await using var db = _c.NovoDb();
        var agora = DateTime.UtcNow;
        var m = new ArquivoMidia(Guid.CreateVersion7(), "a.mp3", titulo, "Banda", tipo, "audio/mpeg", 1000, dono, agora);
        m.ConcluirUpload(1000, "etag", agora);
        m.IniciarAnalise(agora);
        m.RegistrarHashOriginal(new byte[32]);
        m.Aprovar("audio/mpeg", Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray(), tipo == TipoMidia.Musica ? 200m : 30m, 900, ".mp3", new WebRadio.Application.Features.Midias.MidiaOptions().Limite(tipo), agora);
        if (!ativa) m.Desativar();
        db.ArquivosMidia.Add(m);
        await db.SaveChangesAsync();
        return m;
    }

    private async Task<PedidoMusica> PedidoAsync(Guid midiaId, TimeSpan moderadoHa)
    {
        var moderador = await DonoAsync();
        await using var db = _c.NovoDb();
        var criado = DateTime.UtcNow - moderadoHa - TimeSpan.FromMinutes(1);
        var p = new PedidoMusica(Guid.NewGuid(), null, null, "Ana", "Aurora", "Banda", null, null, null, criado);
        p.Aprovar(moderador, midiaId, DateTime.UtcNow - moderadoHa);
        db.PedidosMusica.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private async Task<PedidoParaTocar?> Capturar(Guid instancia)
    {
        await using var db = _c.NovoDb();
        return await new FilaDoPlayout(db).CapturarPedidoAsync(instancia, DateTime.UtcNow, default);
    }

    [SqlFact]
    public async Task Pedido_elegivel_e_entregue_uma_vez_por_instancia_e_reenviado_so_para_outra_instancia()
    {
        await ZerarAsync();
        var dono = await DonoAsync();
        var m = await MidiaAsync(dono);
        var p = await PedidoAsync(m.Id, TimeSpan.FromMinutes(5));
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var primeira = await Capturar(a);
        Assert.Equal((p.Id, m.Id), (primeira!.PedidoId, primeira.MidiaId));
        Assert.Null(await Capturar(a)); // já entregue a esta instância: não volta (a sequência do ao vivo)
        var reenvio = await Capturar(b); // o Liquidsoap reiniciou: a faixa pré-buscada se perdeu, então reenvia
        Assert.Equal(p.Id, reenvio!.PedidoId);
        Assert.Null(await Capturar(b));

        await using var db = _c.NovoDb();
        var salvo = await db.PedidosMusica.SingleAsync(x => x.Id == p.Id);
        Assert.NotNull(salvo.EnviadoPlayoutEmUtc);
        Assert.Equal(b, db.Entry(salvo).Property<Guid?>("EnviadoPlayoutInstancia").CurrentValue);
    }

    [SqlFact]
    public async Task Pedido_aprovado_ha_mais_de_24h_nunca_e_entregue_nem_a_instancia_nova()
    {
        await ZerarAsync();
        var dono = await DonoAsync();
        var m = await MidiaAsync(dono);
        await PedidoAsync(m.Id, TimeSpan.FromHours(25));
        Assert.Null(await Capturar(Guid.NewGuid()));
    }

    [SqlFact]
    public async Task Midia_desativada_depois_do_aprovar_tira_o_pedido_da_fila()
    {
        await ZerarAsync();
        var dono = await DonoAsync();
        var m = await MidiaAsync(dono, ativa: false);
        await PedidoAsync(m.Id, TimeSpan.FromMinutes(5));
        Assert.Null(await Capturar(Guid.NewGuid()));
    }

    [SqlFact]
    public async Task O_pedido_mais_antigo_sai_primeiro()
    {
        await ZerarAsync();
        var dono = await DonoAsync();
        var m1 = await MidiaAsync(dono, titulo: "Um");
        var m2 = await MidiaAsync(dono, titulo: "Dois");
        var maisNovo = await PedidoAsync(m2.Id, TimeSpan.FromMinutes(2));
        var maisAntigo = await PedidoAsync(m1.Id, TimeSpan.FromMinutes(10));
        var instancia = Guid.NewGuid();

        Assert.Equal(maisAntigo.Id, (await Capturar(instancia))!.PedidoId);
        Assert.Equal(maisNovo.Id, (await Capturar(instancia))!.PedidoId);
    }

    [SqlFact]
    public async Task Dois_proxima_simultaneos_com_um_pedido_elegivel_so_um_recebe_o_pedido()
    {
        await ZerarAsync();
        var dono = await DonoAsync();
        var m = await MidiaAsync(dono);
        var p = await PedidoAsync(m.Id, TimeSpan.FromMinutes(5));
        var instancia = Guid.NewGuid();

        var resultados = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => Capturar(instancia))));

        Assert.Equal(1, resultados.Count(r => r is not null)); // nunca o mesmo pedido para duas chamadas
        Assert.Equal(p.Id, resultados.Single(r => r is not null)!.PedidoId);
    }

    // ---- rotação ----

    private async Task<int> SorteioDe(Guid id)
    {
        await using var db = _c.NovoDb();
        return await db.Database.SqlQuery<int>($"SELECT Sorteio AS [Value] FROM midia.ArquivoMidia WHERE Id = {id}").SingleAsync();
    }

    private async Task Tocou(Guid midiaId, TimeSpan ha)
    {
        await using var db = _c.NovoDb();
        db.Reproducoes.Add(new Reproducao(midiaId, null, null, DateTime.UtcNow - ha));
        await db.SaveChangesAsync();
    }

    private async Task<CandidataDaRotacao?> Sortear(TipoMidia tipo, int r, TimeSpan janela, Guid? emVoo = null)
    {
        await using var db = _c.NovoDb();
        return await new FilaDoPlayout(db).SortearForaDaJanelaAsync(tipo, r, DateTime.UtcNow - janela, emVoo ?? Guid.Empty, default);
    }

    [SqlFact]
    public async Task A_rotacao_busca_a_partir_da_chave_volta_ao_inicio_e_so_devolve_aprovada_ativa_do_tipo()
    {
        await ZerarAsync();
        var dono = await DonoAsync();
        var tipo = TipoMidia.Comercial; // um tipo só deste teste: o banco é compartilhado pela classe
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++) ids.Add((await MidiaAsync(dono, tipo, titulo: "C" + i)).Id);
        var desativada = await MidiaAsync(dono, tipo, ativa: false, titulo: "Desativada");
        var outroTipo = await MidiaAsync(dono, TipoMidia.Vinheta, titulo: "Vinheta");
        var chaves = new Dictionary<Guid, int>();
        foreach (var id in ids) chaves[id] = await SorteioDe(id);
        var ordenadas = chaves.OrderBy(k => k.Value).ToList();

        var primeira = await Sortear(tipo, int.MinValue, TimeSpan.FromMinutes(20));
        Assert.Equal(ordenadas[0].Key, primeira!.Id); // Sorteio >= @r: a menor chave

        var apos = await Sortear(tipo, ordenadas[1].Value + 1, TimeSpan.FromMinutes(20));
        Assert.Equal(ordenadas[2].Key, apos!.Id); // a próxima chave depois de @r

        var envolta = await Sortear(tipo, int.MaxValue, TimeSpan.FromMinutes(20));
        Assert.Equal(ordenadas[0].Key, envolta!.Id); // nada >= @r: volta ao início (busca 2)

        Assert.DoesNotContain(desativada.Id, ids);
        Assert.NotEqual(desativada.Id, primeira.Id);
        Assert.NotEqual(outroTipo.Id, primeira.Id);
    }

    [SqlFact]
    public async Task A_janela_de_repeticao_e_a_faixa_em_voo_excluem_candidatas_e_a_busca_3_pega_a_que_tocou_ha_mais_tempo()
    {
        await ZerarAsync();
        var dono = await DonoAsync();
        var tipo = TipoMidia.Musica;
        var a = await MidiaAsync(dono, tipo, titulo: "A");
        var b = await MidiaAsync(dono, tipo, titulo: "B");
        // só estas duas Musica têm reprodução recente; outras Musica de outros testes (se houver) ficam fora do alcance pelo corte curto
        await Tocou(a.Id, TimeSpan.FromMinutes(10));
        await Tocou(b.Id, TimeSpan.FromMinutes(40));

        // janela de 1 min: ninguém tocou nesse período, então a candidata fora da janela existe (nem A nem B são excluídas)
        Assert.NotNull(await Sortear(tipo, int.MinValue, TimeSpan.FromMinutes(1)));

        // janela de 90 min: A e B estão dentro; a busca 3 devolve a que tocou há mais tempo DENTRO do conjunto recente (B), a menos que seja a faixa em voo
        await using (var db = _c.NovoDb())
        {
            var fila = new FilaDoPlayout(db);
            var corte = DateTime.UtcNow.AddMinutes(-90);
            var antiga = await fila.MaisAntigaDaJanelaAsync(tipo, corte, Guid.Empty, default);
            Assert.Equal(b.Id, antiga!.Id);
            var semB = await fila.MaisAntigaDaJanelaAsync(tipo, corte, b.Id, default); // a faixa em voo nunca volta na chamada seguinte
            Assert.Equal(a.Id, semB!.Id);
        }

        // a faixa em voo também sai da busca 1: com uma vinheta só, ela é a candidata, e em voo não volta
        var v = await MidiaAsync(dono, TipoMidia.Vinheta, titulo: "V");
        Assert.Equal(v.Id, (await Sortear(TipoMidia.Vinheta, int.MinValue, TimeSpan.FromMinutes(20)))!.Id);
        Assert.Null(await Sortear(TipoMidia.Vinheta, int.MinValue, TimeSpan.FromMinutes(20), emVoo: v.Id));
    }

    [SqlFact]
    public async Task O_titulo_e_o_artista_vem_do_indice_sem_tocar_a_tabela()
    {
        await ZerarAsync();
        var dono = await DonoAsync();
        var m = await MidiaAsync(dono, TipoMidia.Comercial, titulo: "Título do índice");
        var achada = await Sortear(TipoMidia.Comercial, await SorteioDe(m.Id), TimeSpan.FromMinutes(20));
        Assert.Equal(m.Id, achada!.Id);
        Assert.Equal(("Título do índice", "Banda"), (achada.Titulo, achada.Artista));
    }
}
