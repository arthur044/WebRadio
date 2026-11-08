using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Features.Midias;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Midia;
using Xunit;

namespace WebRadio.Application.Tests.Midias;

/// <summary>E4-F03: <c>PATCH</c> e <c>DELETE /midias/{id}</c> (02 §6, E4-D14).</summary>
public sealed class GerirMidiaTests
{
    private static EditarMidiaCommand Patch(ArquivoMidia m, string? titulo = null, string? artista = null, bool? ativo = null, bool admin = false, byte[]? versao = null,
        bool tituloPresente = false, bool artistaPresente = false, Guid? usuario = null)
        => new(m.Id, versao ?? m.RowVersion, titulo, artista, ativo, admin, tituloPresente || titulo is not null, artistaPresente || artista is not null, usuario ?? m.EnviadoPorUsuarioId);

    [Fact]
    public async Task Edita_titulo_e_artista_normalizados_e_devolve_o_dto()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada();

        var dto = await f.Editar.Handle(Patch(m, titulo: "Novo Ti\u0301tulo", artista: "Outra"), default);

        Assert.Equal("Novo Título", dto.Titulo);
        Assert.Equal("Outra", dto.Artista);
        Assert.Equal("Novo Título", f.Db.ArquivosMidia.Single().Titulo);
    }

    [Fact]
    public async Task Campo_ausente_no_patch_nao_muda_o_que_ja_existe()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada(titulo: "Aurora", artista: "Banda");

        await f.Editar.Handle(Patch(m, titulo: "Só o título"), default);

        Assert.Equal("Banda", f.Db.ArquivosMidia.Single().Artista);
    }

    [Fact]
    public async Task Artista_vazio_no_patch_limpa_o_artista_mas_musica_nao_perde_o_titulo()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada();

        await f.Editar.Handle(Patch(m, artista: "", artistaPresente: true), default);
        Assert.Null(f.Db.ArquivosMidia.Single().Artista);

        var recarregada = f.Db.ArquivosMidia.Single();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Editar.Handle(Patch(recarregada, titulo: "", tituloPresente: true), default));
        Assert.Equal("Aurora", f.Db.ArquivosMidia.Single().Titulo);
    }

    [Fact]
    public async Task Versao_diferente_e_412()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada();
        await Assert.ThrowsAsync<VersaoDesatualizadaException>(() => f.Editar.Handle(Patch(m, titulo: "x", versao: [9, 9, 9, 9, 9, 9, 9, 9]), default));
    }

    [Fact]
    public async Task Ativo_so_o_admin_altera_o_locutor_recebe_403()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada();

        await Assert.ThrowsAsync<AcessoNegadoException>(() => f.Editar.Handle(Patch(m, ativo: false, admin: false), default));
        Assert.True(f.Db.ArquivosMidia.Single().Ativo);

        var dto = await f.Editar.Handle(Patch(m, ativo: false, admin: true), default);
        Assert.False(dto.Ativo);
        var recarregada = f.Db.ArquivosMidia.Single();
        var reativada = await f.Editar.Handle(Patch(recarregada, ativo: true, admin: true), default);
        Assert.True(reativada.Ativo);
    }

    [Fact]
    public async Task Locutor_so_edita_a_propria_midia_o_admin_edita_qualquer_uma()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada();
        var outro = Guid.NewGuid();

        await Assert.ThrowsAsync<AcessoNegadoException>(() => f.Editar.Handle(Patch(m, titulo: "Roubado", usuario: outro), default));
        Assert.Equal("Aurora", f.Db.ArquivosMidia.Single().Titulo);

        await f.Editar.Handle(Patch(m, titulo: "Do dono"), default);
        await f.Editar.Handle(Patch(f.Db.ArquivosMidia.Single(), titulo: "Do admin", admin: true, usuario: outro), default);
        Assert.Equal("Do admin", f.Db.ArquivosMidia.Single().Titulo);
    }

    [Fact]
    public async Task Locutor_nao_edita_nem_a_propria_midia_se_ela_foi_desativada()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada(ativo: false);
        await Assert.ThrowsAsync<AcessoNegadoException>(() => f.Editar.Handle(Patch(m, titulo: "x"), default));
        await f.Editar.Handle(Patch(m, titulo: "x", admin: true), default);
    }

    [Fact]
    public async Task Id_inexistente_e_404()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada();
        await Assert.ThrowsAsync<NaoEncontradoException>(() => f.Editar.Handle(Patch(m, titulo: "x") with { MidiaId = Guid.NewGuid() }, default));
    }

    [Theory]
    [InlineData("tít‮ulo")]
    [InlineData("duas\nlinhas")]
    public void Texto_do_patch_passa_pelo_texto_seguro_e_o_limite_de_200(string texto)
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada();
        var v = new EditarMidiaValidator();
        Assert.False(v.Validate(Patch(m, titulo: texto)).IsValid);
        Assert.False(v.Validate(Patch(m, artista: texto)).IsValid);
        Assert.False(v.Validate(Patch(m, titulo: new string('a', 201))).IsValid);
        Assert.True(v.Validate(Patch(m, titulo: "ok", artista: "ok")).IsValid);
    }

    // ---- DELETE (E4-D14) ----

    [Fact]
    public async Task Excluir_sem_reproducao_apaga_o_registro_e_audita_com_os_dois_hashes_e_quem_excluiu()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada();
        var admin = Guid.NewGuid();

        var r = await f.Excluir.Handle(new ExcluirMidiaCommand(m.Id, admin), default);

        Assert.True(r.Excluida);
        Assert.Empty(f.Db.ArquivosMidia);
        var auditoria = Assert.Single(f.Logs.Linhas, l => l.Contains("MIDIA_EXCLUIDA"));
        Assert.Contains(m.Id.ToString(), auditoria);
        Assert.Contains(string.Concat(Enumerable.Repeat("cd", 32)), auditoria); // HashSHA256
        Assert.Contains(string.Concat(Enumerable.Repeat("ab", 32)), auditoria); // HashOriginalSHA256
        Assert.Contains(f.Usuario.ToString(), auditoria); // EnviadoPorUsuarioId
        Assert.Contains(admin.ToString(), auditoria); // quem excluiu
        Assert.Contains("Aprovado", auditoria); // status na hora
    }

    [Fact]
    public async Task Excluir_com_reproducao_so_desativa_e_preserva_o_historico()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada();
        f.Db.Reproducoes.Add(new Reproducao(m.Id, null, null, f.Relogio.UtcNow));
        f.Db.SaveChanges();

        var r = await f.Excluir.Handle(new ExcluirMidiaCommand(m.Id, Guid.NewGuid()), default);

        Assert.False(r.Excluida);
        Assert.False(r.Midia!.Ativo);
        Assert.False(f.Db.ArquivosMidia.Single().Ativo);
        Assert.Single(f.Db.Reproducoes);
        Assert.DoesNotContain(f.Logs.Linhas, l => l.Contains("MIDIA_EXCLUIDA"));
    }

    [Fact]
    public async Task Excluir_mid_em_analise_e_409_porque_o_worker_esta_com_ela()
    {
        var f = new MidiasFixture();
        var m = f.Semear(StatusSanitizacao.EmAnalise);
        await Assert.ThrowsAsync<TransicaoInvalidaException>(() => f.Excluir.Handle(new ExcluirMidiaCommand(m.Id, Guid.NewGuid()), default));
        Assert.Single(f.Db.ArquivosMidia);
    }

    /// <summary>Um IRadioDbContext que delega ao real e falha o SaveChanges como o SQL falharia (concorrência ou FK).</summary>
    private sealed class DbQueFalhaAoSalvar(WebRadio.Application.Abstracoes.IRadioDbContext real, Exception erro) : WebRadio.Application.Abstracoes.IRadioDbContext
    {
        public Microsoft.EntityFrameworkCore.DbSet<WebRadio.Domain.Usuarios.Usuario> Usuarios => real.Usuarios;
        public Microsoft.EntityFrameworkCore.DbSet<WebRadio.Domain.Autenticacao.RefreshToken> RefreshTokens => real.RefreshTokens;
        public Microsoft.EntityFrameworkCore.DbSet<WebRadio.Domain.Transmissao.CredencialTransmissao> CredenciaisTransmissao => real.CredenciaisTransmissao;
        public Microsoft.EntityFrameworkCore.DbSet<WebRadio.Domain.Grade.Programa> Programas => real.Programas;
        public Microsoft.EntityFrameworkCore.DbSet<WebRadio.Domain.Interacao.PedidoMusica> PedidosMusica => real.PedidosMusica;
        public Microsoft.EntityFrameworkCore.DbSet<ArquivoMidia> ArquivosMidia => real.ArquivosMidia;
        public Microsoft.EntityFrameworkCore.DbSet<WebRadio.Domain.Interacao.Divulgacao> Divulgacoes => real.Divulgacoes;
        public Microsoft.EntityFrameworkCore.DbSet<Reproducao> Reproducoes => real.Reproducoes;

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw erro;
    }

    [Fact]
    public async Task Concorrencia_com_o_Worker_no_DELETE_e_409_de_status_e_nao_audita_uma_exclusao_que_nao_aconteceu()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada();
        var handler = new ExcluirMidiaHandler(new DbQueFalhaAoSalvar(f.Db, new DbUpdateConcurrencyException("RowVersion")), f.Logs.Logger<ExcluirMidiaHandler>());

        var e = await Assert.ThrowsAsync<TransicaoInvalidaException>(() => handler.Handle(new ExcluirMidiaCommand(m.Id, Guid.NewGuid()), default));

        Assert.Contains("mudou de status", e.Message);
        Assert.DoesNotContain(f.Logs.Linhas, l => l.Contains("MIDIA_EXCLUIDA"));
    }

    [Fact]
    public async Task FK_da_reproducao_no_DELETE_e_409_de_comecou_a_tocar_e_nao_audita()
    {
        var f = new MidiasFixture();
        var m = f.SemearAprovada();
        var handler = new ExcluirMidiaHandler(new DbQueFalhaAoSalvar(f.Db, new DbUpdateException("FK")), f.Logs.Logger<ExcluirMidiaHandler>());

        var e = await Assert.ThrowsAsync<TransicaoInvalidaException>(() => handler.Handle(new ExcluirMidiaCommand(m.Id, Guid.NewGuid()), default));

        Assert.Contains("começou a tocar", e.Message);
        Assert.DoesNotContain(f.Logs.Linhas, l => l.Contains("MIDIA_EXCLUIDA"));
    }

    [Fact]
    public async Task Excluir_id_inexistente_e_404()
    {
        var f = new MidiasFixture();
        await Assert.ThrowsAsync<NaoEncontradoException>(() => f.Excluir.Handle(new ExcluirMidiaCommand(Guid.NewGuid(), Guid.NewGuid()), default));
    }
}
