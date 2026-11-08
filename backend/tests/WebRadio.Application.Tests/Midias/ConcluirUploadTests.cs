using FluentValidation;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Midias;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using Xunit;

namespace WebRadio.Application.Tests.Midias;

/// <summary>E4-F02: <c>POST /midias/{id}/concluir</c> (02 §6).</summary>
public sealed class ConcluirUploadTests
{
    private static async Task<(MidiasFixture F, Guid Id)> ComUploadFeitoAsync(long tamanhoNoStorage = 1_000_000, string etag = "\"abc123\"")
    {
        var f = new MidiasFixture();
        var r = await f.Iniciar.Handle(f.Comando(tamanho: 1_000_000), default);
        var m = f.Db.ArquivosMidia.Single();
        f.Storage.Objetos[$"quarentena/{m.ChaveStorage}"] = new InfoObjeto(tamanhoNoStorage, "audio/mpeg", etag);
        return (f, r.MidiaId);
    }

    [Fact]
    public async Task Head_ok_grava_o_etag_sem_aspas_e_vai_a_pendente()
    {
        var (f, id) = await ComUploadFeitoAsync();
        await f.Concluir.Handle(new ConcluirUploadCommand(id, f.Usuario, false), default);

        var m = f.Db.ArquivosMidia.Single();
        Assert.Equal(StatusSanitizacao.Pendente, m.StatusSanitizacao);
        Assert.Equal("abc123", m.EtagUpload);
        Assert.Equal(1_000_000, m.TamanhoBytes);
    }

    [Fact]
    public async Task Objeto_ausente_e_400_e_o_registro_continua_aguardando_upload()
    {
        var (f, id) = await ComUploadFeitoAsync();
        f.Storage.Objetos.Clear();

        await Assert.ThrowsAsync<ValidationException>(() => f.Concluir.Handle(new ConcluirUploadCommand(id, f.Usuario, false), default));
        Assert.Equal(StatusSanitizacao.AguardandoUpload, f.Db.ArquivosMidia.Single().StatusSanitizacao);
    }

    [Theory]
    [InlineData(999_999)]
    [InlineData(1_000_001)]
    public async Task Tamanho_diferente_do_declarado_e_400(long noStorage)
    {
        var (f, id) = await ComUploadFeitoAsync(noStorage);

        await Assert.ThrowsAsync<ValidationException>(() => f.Concluir.Handle(new ConcluirUploadCommand(id, f.Usuario, false), default));
        Assert.Equal(StatusSanitizacao.AguardandoUpload, f.Db.ArquivosMidia.Single().StatusSanitizacao);
    }

    [Fact]
    public async Task Etag_ausente_no_head_e_400()
    {
        var (f, id) = await ComUploadFeitoAsync(etag: "");
        await Assert.ThrowsAsync<ValidationException>(() => f.Concluir.Handle(new ConcluirUploadCommand(id, f.Usuario, false), default));
    }

    [Fact]
    public async Task Concluir_de_outro_usuario_e_404_mas_o_admin_pode()
    {
        var (f, id) = await ComUploadFeitoAsync();
        await Assert.ThrowsAsync<NaoEncontradoException>(() => f.Concluir.Handle(new ConcluirUploadCommand(id, Guid.NewGuid(), false), default));
        Assert.Equal(StatusSanitizacao.AguardandoUpload, f.Db.ArquivosMidia.Single().StatusSanitizacao);

        await f.Concluir.Handle(new ConcluirUploadCommand(id, Guid.NewGuid(), true), default);
        Assert.Equal(StatusSanitizacao.Pendente, f.Db.ArquivosMidia.Single().StatusSanitizacao);
    }

    [Fact]
    public async Task Id_inexistente_e_404()
    {
        var f = new MidiasFixture();
        await Assert.ThrowsAsync<NaoEncontradoException>(() => f.Concluir.Handle(new ConcluirUploadCommand(Guid.NewGuid(), f.Usuario, true), default));
    }

    [Fact]
    public async Task Concluir_duas_vezes_e_recusado_na_segunda()
    {
        var (f, id) = await ComUploadFeitoAsync();
        await f.Concluir.Handle(new ConcluirUploadCommand(id, f.Usuario, false), default);
        await Assert.ThrowsAsync<TransicaoInvalidaException>(() => f.Concluir.Handle(new ConcluirUploadCommand(id, f.Usuario, false), default));
    }

    [Fact]
    public async Task Janela_de_upload_expirada_nao_conclui()
    {
        var (f, id) = await ComUploadFeitoAsync();
        f.Relogio.UtcNow = f.Relogio.UtcNow.AddMinutes(61);
        await Assert.ThrowsAsync<TransicaoInvalidaException>(() => f.Concluir.Handle(new ConcluirUploadCommand(id, f.Usuario, false), default));
    }
}
