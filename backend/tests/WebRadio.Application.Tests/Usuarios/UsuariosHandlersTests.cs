using WebRadio.Application.Features.Usuarios;
using WebRadio.Application.Tests.Auth;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Infrastructure.Seguranca;
using Xunit;

namespace WebRadio.Application.Tests.Usuarios;

/// <summary>02 §3 no nível dos handlers, com banco em memória. O PATCH (trava, RowVersion) é testado contra SQL Server.</summary>
public class UsuariosHandlersTests
{
    private static readonly SenhaHasher Hasher = new();

    [Fact]
    public async Task Listar_ordena_por_nome_filtra_por_papel_e_busca_por_nome_ou_email_sem_diferenciar_caixa_no_email()
    {
        await using var db = AuthFixture.NovoDb();
        AuthFixture.CriarUsuario(db, Hasher, "zeca@example.com", role: Role.Locutor);
        AuthFixture.CriarUsuario(db, Hasher, "Bia@Example.com", role: Role.Admin);
        AuthFixture.CriarUsuario(db, Hasher, "caio@example.com", role: Role.Locutor, ativo: false);
        var handler = new ListarUsuariosHandler(db);

        var todos = await handler.Handle(new ListarUsuariosQuery(null, null, 1, 2), default);
        Assert.Equal(3, todos.Total);
        Assert.Equal(2, todos.Itens.Count);
        Assert.Equal((1, 2), (todos.Pagina, todos.Tamanho));

        var locutores = await handler.Handle(new ListarUsuariosQuery(Role.Locutor, null, 1, 20), default);
        Assert.Equal(2, locutores.Total);
        Assert.All(locutores.Itens, u => Assert.Equal("Locutor", u.Role));

        var porEmail = await handler.Handle(new ListarUsuariosQuery(null, "  bia@EXAMPLE ", 1, 20), default);
        Assert.Equal("Bia@Example.com", Assert.Single(porEmail.Itens).Email);

        Assert.Empty((await handler.Handle(new ListarUsuariosQuery(null, "ninguem", 1, 20), default)).Itens);
        Assert.Empty((await handler.Handle(new ListarUsuariosQuery(null, null, 9, 20), default)).Itens);
    }

    [Theory]
    [InlineData(1, 100, true)]
    [InlineData(1, 101, false)]
    [InlineData(1, 0, false)]
    [InlineData(0, 20, false)]
    public void Validador_da_lista_limita_pagina_e_tamanho(int pagina, int tamanho, bool valido)
        => Assert.Equal(valido, new ListarUsuariosValidator().Validate(new ListarUsuariosQuery(null, null, pagina, tamanho)).IsValid);

    [Fact]
    public void Validador_da_lista_recusa_papel_fora_do_enum_e_busca_enorme()
    {
        var v = new ListarUsuariosValidator();
        Assert.False(v.Validate(new ListarUsuariosQuery((Role)99, null, 1, 20)).IsValid);
        Assert.False(v.Validate(new ListarUsuariosQuery(null, new string('a', 101), 1, 20)).IsValid);
    }

    [Fact]
    public async Task Criar_grava_com_hash_papel_escolhido_e_DeveTrocarSenha_e_recusa_email_repetido()
    {
        await using var db = AuthFixture.NovoDb();
        var handler = new CriarUsuarioHandler(db, Hasher, new RelogioFalso(AuthFixture.Agora));

        var dto = await handler.Handle(new CriarUsuarioCommand(" Bia ", " Bia@Example.com ", AuthFixture.SenhaOk, Role.Admin), default);

        var salvo = db.Usuarios.Single();
        Assert.Equal((salvo.Id, "Bia", "Bia@Example.com", "Admin"), (dto.Id, dto.Nome, dto.Email, dto.Role));
        Assert.True(salvo.DeveTrocarSenha);
        Assert.NotEqual(AuthFixture.SenhaOk, salvo.SenhaHash);
        Assert.Equal(Application.Abstracoes.ResultadoSenha.Ok, Hasher.Verificar(salvo.SenhaHash, AuthFixture.SenhaOk));

        await Assert.ThrowsAsync<EmailJaCadastradoException>(() =>
            handler.Handle(new CriarUsuarioCommand("Outra", "bia@example.COM", AuthFixture.SenhaOk, Role.Ouvinte), default));
        Assert.Equal(1, db.Usuarios.Count());
    }

    [Theory]
    [InlineData("Bia", "bia@example.com", "uma-senha-bem-longa-123", Role.Locutor, true)]
    [InlineData("", "bia@example.com", "uma-senha-bem-longa-123", Role.Locutor, false)]
    [InlineData("Bia", "nao-e-email", "uma-senha-bem-longa-123", Role.Locutor, false)]
    [InlineData("Bia", "bia@example.com", "curta", Role.Locutor, false)]
    [InlineData("Bia", "bia@example.com", "password1234", Role.Locutor, false)]
    [InlineData("Bia", "bia@example.com", "uma-senha-bem-longa-123", (Role)0, false)]
    public void Validador_de_criacao(string nome, string email, string senha, Role role, bool valido)
        => Assert.Equal(valido, new CriarUsuarioValidator(new SenhasVazadas()).Validate(new CriarUsuarioCommand(nome, email, senha, role)).IsValid);

    [Fact]
    public void Validador_de_alteracao_exige_ao_menos_um_campo_e_papel_valido()
    {
        var v = new AlterarUsuarioValidator();
        Assert.False(v.Validate(new AlterarUsuarioCommand(Guid.NewGuid(), null, null, [])).IsValid);
        Assert.False(v.Validate(new AlterarUsuarioCommand(Guid.NewGuid(), (Role)42, null, [])).IsValid);
        Assert.True(v.Validate(new AlterarUsuarioCommand(Guid.NewGuid(), Role.Admin, null, [])).IsValid);
        Assert.True(v.Validate(new AlterarUsuarioCommand(Guid.NewGuid(), null, false, [])).IsValid);
    }

    [Fact]
    public async Task Desbloquear_usa_a_mesma_chave_de_conta_do_login_e_usuario_inexistente_e_NaoEncontrado()
    {
        await using var db = AuthFixture.NovoDb();
        var usuario = AuthFixture.CriarUsuario(db, Hasher, "José@Example.com");
        var throttle = new ThrottleFalso();
        var handler = new DesbloquearLoginHandler(db, throttle);

        await handler.Handle(new DesbloquearLoginCommand(usuario.Id), default);

        Assert.Equal(["JOSE@EXAMPLE.COM"], throttle.Desbloqueadas); // EmailChave: sem diacrítico, maiúsculas
        await Assert.ThrowsAsync<NaoEncontradoException>(() => handler.Handle(new DesbloquearLoginCommand(Guid.NewGuid()), default));
    }

    [Fact]
    public void Comandos_e_DTOs_nao_imprimem_email_nem_senha()
    {
        var textos = new[]
        {
            new CriarUsuarioCommand("Bia", "bia@example.com", "segredo-123456", Role.Locutor).ToString(),
            new ListarUsuariosQuery(null, "bia@example.com", 1, 20).ToString(),
            new UsuarioAdminDto(Guid.NewGuid(), "Bia", "bia@example.com", "Locutor", false, true, AuthFixture.Agora, "").ToString(),
        };

        Assert.All(textos, t => Assert.DoesNotContain("bia@example.com", t, StringComparison.Ordinal));
        Assert.All(textos, t => Assert.DoesNotContain("segredo", t, StringComparison.Ordinal));
    }
}
