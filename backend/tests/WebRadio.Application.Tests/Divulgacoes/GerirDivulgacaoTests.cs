using FluentValidation;
using WebRadio.Application.Features.Divulgacoes;
using WebRadio.Domain.Erros;
using Xunit;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Divulgacoes;

/// <summary>07 E3-F05, E3-D5 e E3-F07: criar, editar (substituição inteira), remover, e o ciclo de vida da imagem.</summary>
public class GerirDivulgacaoTests
{
    private readonly DivulgacoesFixture _f = new();

    private EditarDivulgacaoCommand Edicao(Guid id, byte[]? versao = null, string titulo = "Novo", string? imagem = null, bool ativo = true, string? link = null) =>
        new(id, versao ?? [], titulo, "Msg nova", imagem, link, 10, null, null, ativo);

    [Fact]
    public async Task Cria_com_o_Admin_o_texto_normalizado_e_o_link_na_forma_canonica()
    {
        var dto = await _f.Criar.Handle(new CriarDivulgacaoCommand(_f.Admin, "  Café da manhã  ", "Linha 1\nLinha 2", null, "HTTPS://Exemplo.com/promo", 70, null, null, true), default);

        var d = Assert.Single(_f.Db.Divulgacoes);
        Assert.Equal(_f.Admin, d.CriadoPorUsuarioId);
        Assert.Equal("Café da manhã", dto.Titulo);
        Assert.Equal("Linha 1\nLinha 2", dto.Mensagem);
        Assert.Equal("https://exemplo.com/promo", dto.LinkDestino);
        Assert.Equal(70, dto.Prioridade);
        Assert.True(dto.Ativo);
        Assert.Null(dto.ImagemChave);
        Assert.Null(dto.ImagemUrl);
    }

    [Fact]
    public async Task Ativo_false_no_POST_cria_e_desativa_e_a_divulgacao_nao_aparece_nas_ativas()
    {
        var dto = await _f.CriarAsync(ativo: false);

        Assert.False(dto.Ativo);
        Assert.Empty(await _f.Ativas.Handle(new DivulgacoesAtivasQuery(), default));
    }

    [Fact]
    public async Task Com_imagem_o_DTO_traz_a_chave_e_a_URL_publica_derivada_dela()
    {
        var chave = _f.NovaImagem();

        var dto = await _f.CriarAsync(imagem: chave);

        Assert.Equal(chave, dto.ImagemChave);
        Assert.Equal($"http://localhost:8080/storage/publico/{chave}", dto.ImagemUrl);
    }

    [Fact]
    public async Task Imagem_que_nao_existe_no_bucket_e_400_no_campo_ImagemChave()
    {
        var erro = await Assert.ThrowsAsync<ValidationException>(() => _f.CriarAsync(imagem: ChaveDeImagem.Nova()));

        Assert.Equal("ImagemChave", Assert.Single(erro.Errors).PropertyName);
        Assert.Empty(_f.Db.Divulgacoes);
    }

    [Fact]
    public async Task Imagem_ligada_a_outra_divulgacao_e_409_e_a_propria_divulgacao_pode_reenviar_a_mesma_chave()
    {
        var chave = _f.NovaImagem();
        var primeira = await _f.CriarAsync(imagem: chave);

        await Assert.ThrowsAsync<ImagemEmUsoException>(() => _f.CriarAsync("Outra", chave));

        // O PUT da própria divulgação reenvia a chave que já é dela (o P04 faz isso para manter a imagem).
        var editada = await _f.Editar.Handle(Edicao(primeira.Id, imagem: chave), default);
        Assert.Equal(chave, editada.ImagemChave);
        Assert.Empty(_f.Storage.Apagados);
    }

    [Fact]
    public async Task PUT_substitui_o_recurso_inteiro_chave_ausente_remove_a_imagem_e_a_antiga_e_apagada_depois_do_commit()
    {
        var chave = _f.NovaImagem();
        var criada = await _f.CriarAsync(imagem: chave, link: "https://exemplo.com");

        var editada = await _f.Editar.Handle(Edicao(criada.Id, titulo: "Sem imagem", imagem: null, link: null), default);

        Assert.Null(editada.ImagemChave);
        Assert.Null(editada.ImagemUrl);
        Assert.Null(editada.LinkDestino);
        Assert.Equal("Sem imagem", editada.Titulo);
        Assert.Equal([chave], _f.Storage.Apagados);
        Assert.False(_f.Storage.Objetos.ContainsKey($"publico/{chave}"));
    }

    [Fact]
    public async Task Trocar_a_imagem_apaga_a_antiga_e_liga_a_nova()
    {
        var antiga = _f.NovaImagem();
        var nova = _f.NovaImagem();
        var criada = await _f.CriarAsync(imagem: antiga);

        var editada = await _f.Editar.Handle(Edicao(criada.Id, imagem: nova), default);

        Assert.Equal(nova, editada.ImagemChave);
        Assert.Equal([antiga], _f.Storage.Apagados);
        Assert.True(_f.Storage.Objetos.ContainsKey($"publico/{nova}"));
    }

    [Fact]
    public async Task Falha_do_storage_ao_apagar_a_antiga_so_gera_aviso_e_a_edicao_vale()
    {
        var antiga = _f.NovaImagem();
        var criada = await _f.CriarAsync(imagem: antiga);
        _f.Storage.FalharAoApagar = true;

        var editada = await _f.Editar.Handle(Edicao(criada.Id, imagem: null), default);

        Assert.Null(editada.ImagemChave);
        Assert.Null(_f.Db.Divulgacoes.Single().ImagemChaveStorage);
    }

    [Fact]
    public async Task PUT_com_versao_diferente_da_atual_e_412_e_nada_muda()
    {
        var criada = await _f.CriarAsync();

        await Assert.ThrowsAsync<VersaoDesatualizadaException>(() => _f.Editar.Handle(Edicao(criada.Id, versao: [9, 9]), default));

        Assert.Equal("Promo", _f.Db.Divulgacoes.Single().Titulo);
    }

    [Fact]
    public async Task PUT_de_divulgacao_inexistente_e_404()
    {
        await Assert.ThrowsAsync<NaoEncontradoException>(() => _f.Editar.Handle(Edicao(Guid.NewGuid()), default));
    }

    [Fact]
    public async Task PUT_ativo_false_desativa_e_a_divulgacao_some_das_ativas_ativo_true_volta()
    {
        var criada = await _f.CriarAsync();
        Assert.Single(await _f.Ativas.Handle(new DivulgacoesAtivasQuery(), default));

        var desativada = await _f.Editar.Handle(Edicao(criada.Id, ativo: false), default);
        Assert.False(desativada.Ativo);
        Assert.Empty(await _f.Ativas.Handle(new DivulgacoesAtivasQuery(), default));

        var reativada = await _f.Editar.Handle(Edicao(criada.Id, ativo: true), default);
        Assert.True(reativada.Ativo);
        Assert.Single(await _f.Ativas.Handle(new DivulgacoesAtivasQuery(), default));
    }

    [Fact]
    public async Task Remover_apaga_a_divulgacao_e_a_imagem_e_falha_do_storage_nao_desfaz()
    {
        var chave = _f.NovaImagem();
        var criada = await _f.CriarAsync(imagem: chave);

        await _f.Remover.Handle(new RemoverDivulgacaoCommand(criada.Id), default);

        Assert.Empty(_f.Db.Divulgacoes);
        Assert.Equal([chave], _f.Storage.Apagados);

        var outra = _f.NovaImagem();
        var segunda = await _f.CriarAsync(imagem: outra);
        _f.Storage.FalharAoApagar = true;
        await _f.Remover.Handle(new RemoverDivulgacaoCommand(segunda.Id), default);
        Assert.Empty(_f.Db.Divulgacoes);
    }

    [Fact]
    public async Task A_imagem_so_e_apagada_se_nenhuma_divulgacao_a_usa_a_referencia_e_reconferida_antes_do_delete()
    {
        var chave = _f.NovaImagem();
        var a = await _f.CriarAsync("A", chave);

        // Entre o commit que desligou a imagem de A e o delete, outra divulgação passou a usar a mesma chave: o delete não pode apagá-la.
        await _f.Editar.Handle(Edicao(a.Id, imagem: null), default);
        _f.Db.Divulgacoes.Add(new WebRadio.Domain.Interacao.Divulgacao(Guid.NewGuid(), "B", "M", chave, null, 1, null, null, _f.Admin, Agora));
        await _f.Db.SaveChangesAsync();
        _f.Storage.Apagados.Clear();

        await DivulgacoesDb.ApagarImagemAsync(_f.Db, _f.Storage, chave, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Empty(_f.Storage.Apagados);

        _f.Db.Divulgacoes.RemoveRange(_f.Db.Divulgacoes.Where(d => d.ImagemChaveStorage == chave));
        await _f.Db.SaveChangesAsync();
        await DivulgacoesDb.ApagarImagemAsync(_f.Db, _f.Storage, chave, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Equal([chave], _f.Storage.Apagados);
    }

    [Fact]
    public async Task Remover_inexistente_e_404()
    {
        await Assert.ThrowsAsync<NaoEncontradoException>(() => _f.Remover.Handle(new RemoverDivulgacaoCommand(Guid.NewGuid()), default));
    }

    [Fact]
    public async Task GET_por_id_inexistente_e_404_e_existente_traz_a_versao()
    {
        await Assert.ThrowsAsync<NaoEncontradoException>(() => _f.PorId.Handle(new DivulgacaoPorIdQuery(Guid.NewGuid()), default));

        var criada = await _f.CriarAsync();
        Assert.Equal(criada.Id, (await _f.PorId.Handle(new DivulgacaoPorIdQuery(criada.Id), default)).Id);
    }
}
