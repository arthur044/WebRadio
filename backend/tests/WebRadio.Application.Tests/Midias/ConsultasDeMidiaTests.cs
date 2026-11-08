using WebRadio.Application.Features.Midias;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using Xunit;

namespace WebRadio.Application.Tests.Midias;

/// <summary>E4-F03: <c>GET /midias</c>, <c>GET /midias/{id}</c> e <c>GET /midias/{id}/preview</c> (02 §6, S-M07).</summary>
public sealed class ConsultasDeMidiaTests
{
    private static ListarMidiasQuery Lista(bool admin = true, TipoMidia? tipo = null, StatusSanitizacao? status = null, bool? ativo = null, string? busca = null, int pagina = 1, int tamanho = 20)
        => new(tipo, status, ativo, busca, pagina, tamanho, admin);

    [Fact]
    public async Task Lista_filtra_por_tipo_e_por_status()
    {
        var f = new MidiasFixture();
        f.SemearAprovada(tipo: TipoMidia.Musica);
        f.SemearAprovada(titulo: null, tipo: TipoMidia.Vinheta);
        f.Semear(StatusSanitizacao.Pendente);

        Assert.Equal(3, (await f.Listar.Handle(Lista(), default)).Total);
        Assert.Single((await f.Listar.Handle(Lista(tipo: TipoMidia.Musica), default)).Itens);
        Assert.Equal(2, (await f.Listar.Handle(Lista(tipo: TipoMidia.Vinheta), default)).Total); // a aprovada e a pendente (vinheta)
        Assert.Single((await f.Listar.Handle(Lista(status: StatusSanitizacao.Pendente), default)).Itens);
    }

    [Fact]
    public async Task Locutor_so_ve_as_ativas_e_o_filtro_ativo_so_vale_para_o_admin()
    {
        var f = new MidiasFixture();
        f.SemearAprovada(titulo: "Ativa");
        f.SemearAprovada(titulo: "Desativada", ativo: false);

        Assert.Single((await f.Listar.Handle(Lista(admin: false), default)).Itens);
        Assert.Single((await f.Listar.Handle(Lista(admin: false, ativo: false), default)).Itens); // ignorado: segue só as ativas
        Assert.Equal(2, (await f.Listar.Handle(Lista(), default)).Total);
        Assert.Equal("Desativada", Assert.Single((await f.Listar.Handle(Lista(ativo: false), default)).Itens).Titulo);
        Assert.Equal("Ativa", Assert.Single((await f.Listar.Handle(Lista(ativo: true), default)).Itens).Titulo);
    }

    [Fact]
    public async Task Busca_em_titulo_artista_e_nome_do_arquivo_sem_diferenciar_caixa()
    {
        var f = new MidiasFixture();
        f.SemearAprovada(titulo: "Aurora Boreal", artista: "Zeca", nome: "x1.mp3");
        f.SemearAprovada(titulo: "Outra", artista: "Marina Lima", nome: "x2.mp3");
        f.SemearAprovada(titulo: "Terceira", artista: "Zeca", nome: "vinheta-manha.mp3");

        Assert.Single((await f.Listar.Handle(Lista(busca: "aurora"), default)).Itens);
        Assert.Single((await f.Listar.Handle(Lista(busca: "MARINA"), default)).Itens);
        Assert.Single((await f.Listar.Handle(Lista(busca: "manha"), default)).Itens);
        Assert.Equal(2, (await f.Listar.Handle(Lista(busca: "zeca"), default)).Total);
    }

    [Theory]
    [InlineData("100%")]
    [InlineData("a_b")]
    public async Task Busca_trata_percent_e_underscore_como_texto_literal(string literal)
    {
        var f = new MidiasFixture();
        f.SemearAprovada(titulo: "Hit " + literal + " fim");
        f.SemearAprovada(titulo: "Hit 1000 fim");
        f.SemearAprovada(titulo: "Hit axb fim");
        f.SemearAprovada(titulo: "Hit a fim");

        // O coringa NÃO pode casar as outras: só a que contém o texto literal. (Colchete e barra, que o InMemory não reproduz, estão no MidiasSqlTests.)
        var r = await f.Listar.Handle(Lista(busca: literal), default);
        Assert.Equal("Hit " + literal + " fim", Assert.Single(r.Itens).Titulo);
    }

    [Fact]
    public async Task Paginacao_ordena_da_mais_nova_para_a_mais_antiga_e_informa_o_total()
    {
        var f = new MidiasFixture();
        for (var i = 0; i < 5; i++)
        {
            f.Relogio.UtcNow = f.Relogio.UtcNow.AddMinutes(1);
            f.SemearAprovada(titulo: $"M{i}");
        }

        var p1 = await f.Listar.Handle(Lista(pagina: 1, tamanho: 2), default);
        var p3 = await f.Listar.Handle(Lista(pagina: 3, tamanho: 2), default);
        Assert.Equal(["M4", "M3"], p1.Itens.Select(i => i.Titulo));
        Assert.Equal(["M0"], p3.Itens.Select(i => i.Titulo));
        Assert.Equal(5, p1.Total);
    }

    [Fact]
    public async Task Hash_so_para_o_admin_em_hex_e_enviadoPorNome_vem_do_usuario()
    {
        var f = new MidiasFixture();
        var dono = new WebRadio.Domain.Usuarios.Usuario(Guid.NewGuid(), "Ana Locutora", "ana@example.com", "hash", Role.Locutor, false, f.Relogio.UtcNow);
        f.Db.Usuarios.Add(dono);
        f.Db.SaveChanges();
        f.SemearAprovada(usuario: dono.Id);

        var admin = Assert.Single((await f.Listar.Handle(Lista(admin: true), default)).Itens);
        var locutor = Assert.Single((await f.Listar.Handle(Lista(admin: false), default)).Itens);
        Assert.Equal(string.Concat(Enumerable.Repeat("cd", 32)), admin.HashSha256);
        Assert.Equal(string.Concat(Enumerable.Repeat("ab", 32)), admin.HashOriginalSha256);
        Assert.Null(locutor.HashSha256);
        Assert.Null(locutor.HashOriginalSha256);
        Assert.Equal("Ana Locutora", admin.EnviadoPorNome);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void Pagina_e_tamanho_fora_da_faixa_sao_invalidos(int pagina, int tamanho)
        => Assert.False(new ListarMidiasValidator().Validate(Lista(pagina: pagina, tamanho: tamanho)).IsValid);

    [Fact]
    public void Busca_muito_longa_e_invalida()
        => Assert.False(new ListarMidiasValidator().Validate(Lista(busca: new string('a', 201))).IsValid);

    [Fact]
    public async Task Por_id_devolve_a_versao_para_o_etag_e_404_se_nao_existe()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada();

        var dto = await f.PorId.Handle(new MidiaPorIdQuery(m.Id, true), default);
        Assert.Equal(m.Id, dto.Id);
        Assert.Equal(Convert.ToBase64String(m.RowVersion), dto.Versao);
        await Assert.ThrowsAsync<NaoEncontradoException>(() => f.PorId.Handle(new MidiaPorIdQuery(Guid.NewGuid(), true), default));
    }

    [Fact]
    public async Task Locutor_nao_ve_por_id_uma_desativada()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada(ativo: false);
        await Assert.ThrowsAsync<NaoEncontradoException>(() => f.PorId.Handle(new MidiaPorIdQuery(m.Id, false), default));
        await f.PorId.Handle(new MidiaPorIdQuery(m.Id, true), default);
    }

    // ---- preview (S-M07, S-A03) ----

    [Fact]
    public async Task Preview_assina_o_GET_de_5_minutos_em_midia_com_audio_mpeg_e_inline()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada();

        var p = await f.Preview.Handle(new PreviewDaMidiaQuery(m.Id, true), default);

        var chamada = Assert.Single(f.Storage.Previews);
        Assert.Equal(("midia", m.ChaveStorage, TimeSpan.FromMinutes(5), "audio/mpeg"), (chamada.Bucket, chamada.Chave, chamada.Validade, chamada.ContentType));
        Assert.Equal($"inline; filename*=UTF-8''{m.Id}.mp3", chamada.Disposition);
        Assert.Contains("/storage/midia/", p.Url);
        Assert.Equal(f.Relogio.UtcNow.AddMinutes(5), p.ExpiraEmUtc);
    }

    [Theory]
    [InlineData(StatusSanitizacao.AguardandoUpload)]
    [InlineData(StatusSanitizacao.Pendente)]
    [InlineData(StatusSanitizacao.EmAnalise)]
    [InlineData(StatusSanitizacao.Rejeitado)]
    public async Task Preview_de_midia_nao_aprovada_e_404_e_nunca_assina_URL_da_quarentena(StatusSanitizacao status)
    {
        var f = new MidiasFixture();
        var m = f.Semear(status);

        await Assert.ThrowsAsync<NaoEncontradoException>(() => f.Preview.Handle(new PreviewDaMidiaQuery(m.Id, true), default));
        Assert.Empty(f.Storage.Previews);
    }

    [Fact]
    public async Task Preview_de_desativada_e_404_para_o_locutor_e_funciona_para_o_admin()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada(ativo: false);
        await Assert.ThrowsAsync<NaoEncontradoException>(() => f.Preview.Handle(new PreviewDaMidiaQuery(m.Id, false), default));
        Assert.Empty(f.Storage.Previews);
        await f.Preview.Handle(new PreviewDaMidiaQuery(m.Id, true), default);
    }

    [Fact]
    public async Task Preview_de_id_inexistente_e_404()
    {
        var f = new MidiasFixture();
        await Assert.ThrowsAsync<NaoEncontradoException>(() => f.Preview.Handle(new PreviewDaMidiaQuery(Guid.NewGuid(), true), default));
    }
}
