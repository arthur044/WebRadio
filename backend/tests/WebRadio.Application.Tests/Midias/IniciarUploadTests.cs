using FluentValidation;
using WebRadio.Application.Features.Midias;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using Xunit;

namespace WebRadio.Application.Tests.Midias;

/// <summary>E4-F02: <c>POST /midias/uploads</c> (02 §6, S-M01, S-M06, S-M07).</summary>
public sealed class IniciarUploadTests
{
    private static readonly IniciarUploadValidator Validador = new(new MidiaOptions());

    [Fact]
    public async Task Cria_o_registro_aguardando_upload_na_quarentena_e_assina_a_policy_com_o_tamanho_declarado()
    {
        var f = new MidiasFixture();
        var r = await f.Iniciar.Handle(f.Comando(nome: "Vinheta Manhã.mp3", tamanho: 123_456, artista: "Rádio"), default);

        var m = f.Db.ArquivosMidia.Single();
        Assert.Equal(r.MidiaId, m.Id);
        Assert.Equal(StatusSanitizacao.AguardandoUpload, m.StatusSanitizacao);
        Assert.Equal("quarentena", m.Bucket);
        Assert.Equal($"{f.Relogio.UtcNow:yyyy}/{f.Relogio.UtcNow:MM}/{m.Id}", m.ChaveStorage);
        Assert.Equal(f.Usuario, m.EnviadoPorUsuarioId);
        Assert.Equal(123_456, m.TamanhoBytes);

        var p = Assert.Single(f.Storage.Policies);
        Assert.Equal(("quarentena", m.ChaveStorage, "audio/mpeg", 123_456L, TimeSpan.FromMinutes(15)), p);
        Assert.Equal(m.ChaveStorage, r.Upload.Campos["key"]);
        Assert.StartsWith("http", r.Upload.Url);
    }

    [Fact]
    public async Task O_registro_so_nasce_se_a_policy_for_assinada()
    {
        var f = new MidiasFixture();
        f.Storage.FalharNaPolicy = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Iniciar.Handle(f.Comando(), default));
        Assert.Empty(f.Db.ArquivosMidia);
    }

    [Fact]
    public async Task O_registro_vive_pelo_prazo_de_limpeza_e_nao_pelo_da_policy()
    {
        var f = new MidiasFixture();
        await f.Iniciar.Handle(f.Comando(), default);
        Assert.Equal(f.Relogio.UtcNow.AddHours(2), f.Db.ArquivosMidia.Single().UploadExpiraEmUtc);

        var g = new MidiasFixture(new MidiaOptions { Upload = new UploadOptions { LimpezaAguardandoHoras = 3 } });
        await g.Iniciar.Handle(g.Comando(), default);
        Assert.Equal(g.Relogio.UtcNow.AddHours(3), g.Db.ArquivosMidia.Single().UploadExpiraEmUtc);
    }

    [Fact]
    public async Task Retry_after_do_limite_de_abertos_vai_ate_o_vencimento_do_aguardando_mais_antigo()
    {
        var f = new MidiasFixture();
        f.Semear(StatusSanitizacao.AguardandoUpload, criadaEm: f.Relogio.UtcNow.AddMinutes(-30)); // vence em 90 min
        f.Semear(StatusSanitizacao.AguardandoUpload, criadaEm: f.Relogio.UtcNow.AddMinutes(-10));
        for (var i = 0; i < 3; i++) f.Semear(StatusSanitizacao.Pendente);

        var e = await Assert.ThrowsAsync<LimiteExcedidoException>(() => f.Iniciar.Handle(f.Comando(), default));
        Assert.Equal(TimeSpan.FromMinutes(90), e.TenteDeNovoEm);
    }

    [Fact]
    public async Task Retry_after_do_limite_de_abertos_tem_o_minimo_de_60_s()
    {
        var f = new MidiasFixture();
        f.Semear(StatusSanitizacao.AguardandoUpload, criadaEm: f.Relogio.UtcNow.AddMinutes(-119).AddSeconds(-40)); // vence em 20 s
        for (var i = 0; i < 4; i++) f.Semear(StatusSanitizacao.Pendente);

        var e = await Assert.ThrowsAsync<LimiteExcedidoException>(() => f.Iniciar.Handle(f.Comando(), default));
        Assert.Equal(TimeSpan.FromSeconds(60), e.TenteDeNovoEm);
    }

    [Fact]
    public async Task Retry_after_sem_aguardando_aberto_e_de_60_s()
    {
        var f = new MidiasFixture();
        for (var i = 0; i < 3; i++) f.Semear(StatusSanitizacao.Pendente);
        for (var i = 0; i < 2; i++) f.Semear(StatusSanitizacao.EmAnalise);

        var e = await Assert.ThrowsAsync<LimiteExcedidoException>(() => f.Iniciar.Handle(f.Comando(), default));
        Assert.Equal(TimeSpan.FromSeconds(60), e.TenteDeNovoEm);
    }

    [Fact]
    public async Task Aguardando_upload_vencido_nao_conta_no_limite_de_abertos()
    {
        var f = new MidiasFixture();
        for (var i = 0; i < 3; i++) f.Semear(StatusSanitizacao.AguardandoUpload, criadaEm: f.Relogio.UtcNow.AddHours(-3)); // venceram há 1 h
        for (var i = 0; i < 2; i++) f.Semear(StatusSanitizacao.Pendente);

        for (var i = 0; i < 3; i++) await f.Iniciar.Handle(f.Comando(), default); // 2 abertos de verdade: cabem mais 3
        await Assert.ThrowsAsync<LimiteExcedidoException>(() => f.Iniciar.Handle(f.Comando(), default));
    }

    [Fact]
    public async Task Se_a_assinatura_da_policy_falha_depois_do_commit_o_registro_e_descartado()
    {
        var f = new MidiasFixture();
        f.Storage.FalharNaPolicy = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Iniciar.Handle(f.Comando(), default));
        Assert.Empty(f.Db.ArquivosMidia);
    }

    [Fact]
    public async Task A_policy_so_e_assinada_depois_do_commit_nunca_quando_o_limite_ou_a_trava_recusam()
    {
        var f = new MidiasFixture();
        for (var i = 0; i < 5; i++) f.Semear(StatusSanitizacao.Pendente);
        await Assert.ThrowsAsync<LimiteExcedidoException>(() => f.Iniciar.Handle(f.Comando(), default));
        Assert.Empty(f.Storage.Policies);

        var g = new MidiasFixture();
        g.Transacao.Ocupada = true;
        var e = await Assert.ThrowsAsync<LimiteExcedidoException>(() => g.Iniciar.Handle(g.Comando(), default));
        Assert.Equal(TimeSpan.FromSeconds(1), e.TenteDeNovoEm);
        Assert.Empty(g.Storage.Policies);
        Assert.Empty(g.Db.ArquivosMidia);
    }

    [Fact]
    public async Task Conta_e_insere_sob_a_trava_do_usuario()
    {
        var f = new MidiasFixture();
        await f.Iniciar.Handle(f.Comando(), default);

        var trava = Assert.Single(f.Transacao.ConjuntosDeTravas);
        Assert.StartsWith("midia:upload:", trava);
        Assert.DoesNotContain(f.Usuario.ToString("N"), trava);
    }

    [Theory]
    [InlineData(StatusSanitizacao.AguardandoUpload)]
    [InlineData(StatusSanitizacao.Pendente)]
    [InlineData(StatusSanitizacao.EmAnalise)]
    public async Task O_sexto_aberto_e_recusado_com_429(StatusSanitizacao aberto)
    {
        var f = new MidiasFixture();
        for (var i = 0; i < 5; i++) f.Semear(aberto);

        var e = await Assert.ThrowsAsync<LimiteExcedidoException>(() => f.Iniciar.Handle(f.Comando(), default));
        Assert.True(e.TenteDeNovoEm > TimeSpan.Zero);
        Assert.Equal(5, f.Db.ArquivosMidia.Count());
    }

    [Fact]
    public async Task Quatro_abertos_deixam_o_quinto_passar_e_aprovados_e_rejeitados_nao_contam()
    {
        var f = new MidiasFixture();
        for (var i = 0; i < 3; i++) f.Semear(StatusSanitizacao.Pendente);
        f.Semear(StatusSanitizacao.Rejeitado);
        f.Semear(StatusSanitizacao.Rejeitado);
        f.Semear(StatusSanitizacao.AguardandoUpload, usuario: Guid.NewGuid());

        await f.Iniciar.Handle(f.Comando(), default);
        await f.Iniciar.Handle(f.Comando(), default);
        await Assert.ThrowsAsync<LimiteExcedidoException>(() => f.Iniciar.Handle(f.Comando(), default));
    }

    [Fact]
    public async Task O_21o_na_mesma_hora_e_recusado_pelo_redis_com_o_tempo_restante_da_janela()
    {
        var f = new MidiasFixture(new MidiaOptions { MaximoAbertos = 100 });
        for (var i = 0; i < 20; i++) await f.Iniciar.Handle(f.Comando(), default);

        var e = await Assert.ThrowsAsync<LimiteExcedidoException>(() => f.Iniciar.Handle(f.Comando(), default));
        Assert.InRange(e.TenteDeNovoEm, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));
        Assert.Equal(20, f.Db.ArquivosMidia.Count());
    }

    private static async Task<(long Bytes, long Contagem)> ContadoresAsync(MidiasFixture f)
    {
        var bytes = await f.Redis.LerAsync($"throttle:{LimiteDeUploadDeMidia.ChaveDosBytes(f.Hasher, f.Usuario)}");
        var contagem = await f.Redis.LerAsync($"throttle:midiaup:{LimiteDeUploadDeMidia.ChaveOpaca(f.Hasher, f.Usuario)}");
        return (bytes, contagem);
    }

    [Fact]
    public async Task Recusa_por_5_abertos_por_trava_ocupada_ou_por_falha_da_assinatura_devolve_os_bytes_e_a_tentativa()
    {
        var f = new MidiasFixture();
        for (var i = 0; i < 5; i++) f.Semear(StatusSanitizacao.Pendente);
        await Assert.ThrowsAsync<LimiteExcedidoException>(() => f.Iniciar.Handle(f.Comando(tamanho: 250_000), default));
        Assert.Equal((0L, 0L), await ContadoresAsync(f)); // 5 abertos: nenhum envio nasceu, nada consumido

        var g = new MidiasFixture();
        g.Transacao.Ocupada = true;
        await Assert.ThrowsAsync<LimiteExcedidoException>(() => g.Iniciar.Handle(g.Comando(tamanho: 250_000), default));
        Assert.Equal((0L, 0L), await ContadoresAsync(g));

        var h = new MidiasFixture();
        h.Storage.FalharNaPolicy = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Iniciar.Handle(h.Comando(tamanho: 250_000), default));
        Assert.Equal((0L, 0L), await ContadoresAsync(h));
    }

    [Fact]
    public async Task Envio_que_nasceu_consome_os_contadores_e_so_o_cancelamento_nunca_devolve()
    {
        var f = new MidiasFixture();
        await f.Iniciar.Handle(f.Comando(tamanho: 250_000), default);
        Assert.Equal((250_000L, 1L), await ContadoresAsync(f));
        await f.Excluir.Handle(new ExcluirMidiaCommand(f.Db.ArquivosMidia.Single().Id, f.Usuario, EhAdmin: false), default);
        Assert.Equal((250_000L, 1L), await ContadoresAsync(f));
    }

    private static MidiaOptions ComOrcamentoDe3MiB() => new() { MaximoAbertos = 100, BytesDeclaradosPorHora = 3 * 1024 * 1024 };

    [Fact]
    public async Task O_orcamento_de_bytes_recusa_o_post_que_passa_do_limite_e_o_recusado_nao_come_o_orcamento()
    {
        var f = new MidiasFixture(ComOrcamentoDe3MiB());
        for (var i = 0; i < 3; i++) await f.Iniciar.Handle(f.Comando(tamanho: 1_000_000), default); // 3.000.000 de 3.145.728

        var e = await Assert.ThrowsAsync<LimiteExcedidoException>(() => f.Iniciar.Handle(f.Comando(tamanho: 1_000_000), default));
        Assert.InRange(e.TenteDeNovoEm, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));
        Assert.Equal(3, f.Db.ArquivosMidia.Count());

        await f.Iniciar.Handle(f.Comando(tamanho: 100_000), default); // cabe: a recusa devolveu os bytes que somou
        await Assert.ThrowsAsync<LimiteExcedidoException>(() => f.Iniciar.Handle(f.Comando(tamanho: 1_000_000), default));
    }

    [Fact]
    public async Task O_cancelamento_nao_devolve_o_orcamento_de_bytes_nem_o_20_por_hora()
    {
        var f = new MidiasFixture(ComOrcamentoDe3MiB());
        for (var i = 0; i < 3; i++) await f.Iniciar.Handle(f.Comando(tamanho: 1_000_000), default);
        foreach (var m in f.Db.ArquivosMidia.ToList()) await f.Excluir.Handle(new ExcluirMidiaCommand(m.Id, f.Usuario, EhAdmin: false), default);
        Assert.All(f.Db.ArquivosMidia, m => Assert.True(m.Cancelado)); // as linhas ficam

        await Assert.ThrowsAsync<LimiteExcedidoException>(() => f.Iniciar.Handle(f.Comando(tamanho: 1_000_000), default));
    }

    [Fact]
    public async Task Com_o_redis_fora_o_orcamento_vem_do_sql_inclusive_as_canceladas_e_o_retry_after_e_a_saida_da_mais_antiga()
    {
        var f = new MidiasFixture(ComOrcamentoDe3MiB());
        f.Redis.Fora = true;
        await f.Iniciar.Handle(f.Comando(tamanho: 1_000_000), default); // t0
        f.Relogio.UtcNow = f.Relogio.UtcNow.AddMinutes(10);
        await f.Iniciar.Handle(f.Comando(tamanho: 1_000_000), default); // t0 + 10 min
        f.Relogio.UtcNow = f.Relogio.UtcNow.AddMinutes(10);
        await f.Iniciar.Handle(f.Comando(tamanho: 1_000_000), default); // t0 + 20 min
        foreach (var m in f.Db.ArquivosMidia.ToList()) await f.Excluir.Handle(new ExcluirMidiaCommand(m.Id, f.Usuario, EhAdmin: false), default); // canceladas contam igual

        f.Relogio.UtcNow = f.Relogio.UtcNow.AddMinutes(10); // t0 + 30 min
        var e = await Assert.ThrowsAsync<LimiteExcedidoException>(() => f.Iniciar.Handle(f.Comando(tamanho: 1_000_000), default));
        Assert.Equal(TimeSpan.FromMinutes(30), e.TenteDeNovoEm); // a mais antiga (t0) sai da janela em t0 + 60 min

        f.Relogio.UtcNow = f.Relogio.UtcNow.AddMinutes(31);
        await f.Iniciar.Handle(f.Comando(tamanho: 1_000_000), default);
    }

    [Fact]
    public async Task Outro_usuario_tem_a_propria_cota()
    {
        var f = new MidiasFixture(new MidiaOptions { UploadsPorHora = 1 });
        await f.Iniciar.Handle(f.Comando(), default);
        await f.Iniciar.Handle(f.Comando(usuario: Guid.NewGuid()), default);
    }

    [Fact]
    public async Task Com_o_redis_fora_o_limite_de_20_por_hora_vem_do_sql()
    {
        var f = new MidiasFixture(new MidiaOptions { MaximoAbertos = 100 });
        f.Redis.Fora = true;
        // 20 mídias recentes (aprovadas ou rejeitadas: o limite por hora conta todas as criadas, não só as abertas).
        for (var i = 0; i < 19; i++) f.Semear(StatusSanitizacao.Rejeitado, criadaEm: f.Relogio.UtcNow.AddMinutes(-30));
        f.Semear(StatusSanitizacao.Rejeitado, criadaEm: f.Relogio.UtcNow.AddMinutes(-10));

        var e = await Assert.ThrowsAsync<LimiteExcedidoException>(() => f.Iniciar.Handle(f.Comando(), default));
        // A mais antiga da janela sai em 30 min (criada há 30, janela de 60).
        Assert.InRange(e.TenteDeNovoEm, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
    }

    [Fact]
    public async Task Com_o_redis_fora_as_mais_velhas_que_uma_hora_nao_contam()
    {
        var f = new MidiasFixture(new MidiaOptions { MaximoAbertos = 100 });
        f.Redis.Fora = true;
        for (var i = 0; i < 25; i++) f.Semear(StatusSanitizacao.Rejeitado, criadaEm: f.Relogio.UtcNow.AddMinutes(-61));

        await f.Iniciar.Handle(f.Comando(), default);
    }

    [Theory]
    [InlineData("a/b.mp3")]
    [InlineData("a\\b.mp3")]
    [InlineData("c:evil.mp3")]
    [InlineData("nome‮txt.3pm")] // bidi
    [InlineData("nome\u0007.mp3")] // controle
    [InlineData("")]
    public void Nome_original_com_barra_dois_pontos_controle_ou_bidi_e_invalido(string nome)
        => Assert.False(Validador.Validate(new MidiasFixture().Comando(nome: nome)).IsValid);

    [Fact]
    public void Nome_original_de_ate_255_caracteres_vale_e_256_nao()
    {
        var f = new MidiasFixture();
        Assert.True(Validador.Validate(f.Comando(nome: new string('a', 251) + ".mp3")).IsValid);
        Assert.False(Validador.Validate(f.Comando(nome: new string('a', 252) + ".mp3")).IsValid);
    }

    [Fact]
    public void Musica_exige_titulo_e_vinheta_nao()
    {
        var f = new MidiasFixture();
        Assert.False(Validador.Validate(f.Comando(tipo: TipoMidia.Musica)).IsValid);
        Assert.True(Validador.Validate(f.Comando(tipo: TipoMidia.Musica, titulo: "Aurora")).IsValid);
        Assert.True(Validador.Validate(f.Comando(tipo: TipoMidia.Vinheta)).IsValid);
    }

    [Theory]
    [InlineData("tít‮ulo")]
    [InlineData("duas\nlinhas")]
    public void Titulo_e_artista_passam_pelo_texto_seguro(string texto)
    {
        var f = new MidiasFixture();
        Assert.False(Validador.Validate(f.Comando(titulo: texto)).IsValid);
        Assert.False(Validador.Validate(f.Comando(artista: texto)).IsValid);
        Assert.False(Validador.Validate(f.Comando(titulo: new string('a', 201))).IsValid);
    }

    [Theory]
    [InlineData("audio/mpeg", true)]
    [InlineData("audio/ogg", true)]
    [InlineData("audio/wav", true)]
    [InlineData("audio/x-wav", true)]
    [InlineData("audio/flac", true)]
    [InlineData("audio/mp4", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("audio/mpeg; charset=x", false)]
    [InlineData("text/html", false)]
    public void Mime_declarado_so_da_lista_fechada(string mime, bool valido)
        => Assert.Equal(valido, Validador.Validate(new MidiasFixture().Comando(mime: mime)).IsValid);

    [Theory]
    [InlineData(TipoMidia.Vinheta, 10 * 1024 * 1024)]
    [InlineData(TipoMidia.Comercial, 20 * 1024 * 1024)]
    [InlineData(TipoMidia.Musica, 250 * 1024 * 1024)]
    public void Tamanho_ate_o_limite_do_tipo_vale_e_um_byte_a_mais_nao(TipoMidia tipo, long limite)
    {
        var f = new MidiasFixture();
        var titulo = tipo == TipoMidia.Musica ? "x" : null;
        Assert.True(Validador.Validate(f.Comando(tipo: tipo, tamanho: limite, titulo: titulo)).IsValid);
        Assert.False(Validador.Validate(f.Comando(tipo: tipo, tamanho: limite + 1, titulo: titulo)).IsValid);
        Assert.False(Validador.Validate(f.Comando(tipo: tipo, tamanho: 0, titulo: titulo)).IsValid);
    }

    [Fact]
    public void O_tipo_precisa_ser_um_valor_conhecido()
        => Assert.False(Validador.Validate(new MidiasFixture().Comando(tipo: (TipoMidia)9)).IsValid);
}
