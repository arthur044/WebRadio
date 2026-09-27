using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Usuarios;
using Xunit;

namespace WebRadio.Domain.Tests.Usuarios;

public class UsuarioTests
{
    private static readonly DateTime Agora = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private static Usuario CriarAdmin(bool deveTrocarSenha = false) =>
        new(Guid.NewGuid(), "Ana Admin", "ana@example.com", "hash-1", Role.Admin, deveTrocarSenha, Agora);

    [Fact]
    public void Construtor_normaliza_email_e_comeca_ativo()
    {
        var usuario = new Usuario(Guid.NewGuid(), "Bia Locutora", "Bia@Example.com", "hash", Role.Locutor, false, Agora);

        Assert.Equal("BIA@EXAMPLE.COM", usuario.EmailNormalizado);
        Assert.True(usuario.Ativo);
        Assert.False(usuario.DeveTrocarSenha);
    }

    [Fact]
    public void Construtor_rejeita_data_nao_utc()
    {
        Assert.Throws<ArgumentException>(() =>
            new Usuario(Guid.NewGuid(), "X", "x@example.com", "hash", Role.Ouvinte, false, DateTime.Now));
    }

    [Fact]
    public void AlterarRole_de_locutor_para_admin_funciona_e_dispara_evento()
    {
        var usuario = new Usuario(Guid.NewGuid(), "Bia", "bia@example.com", "hash", Role.Locutor, false, Agora);

        usuario.AlterarRole(Role.Admin, ehUltimoAdminAtivo: false, Agora.AddMinutes(1));

        Assert.Equal(Role.Admin, usuario.Role);
        var evento = Assert.IsType<UsuarioCredenciaisAlteradas>(Assert.Single(usuario.DomainEvents));
        Assert.Equal(usuario.Id, evento.UsuarioId);
        Assert.Equal(MotivoAlteracaoCredenciais.RoleAlterada, evento.Motivo);
    }

    [Fact]
    public void AlterarRole_rebaixando_o_unico_admin_ativo_lanca_TransicaoInvalida()
    {
        var admin = CriarAdmin();

        var ex = Assert.Throws<TransicaoInvalidaException>(() =>
            admin.AlterarRole(Role.Locutor, ehUltimoAdminAtivo: true, Agora.AddMinutes(1)));

        Assert.Contains("último Admin", ex.Message);
        Assert.Empty(admin.DomainEvents);
    }

    [Fact]
    public void AlterarRole_admin_para_admin_nao_conta_como_remover_o_ultimo_admin()
    {
        var admin = CriarAdmin();

        // "Alterar" para o mesmo papel Admin não remove o último Admin — deve passar mesmo com a flag true.
        admin.AlterarRole(Role.Admin, ehUltimoAdminAtivo: true, Agora.AddMinutes(1));

        Assert.Equal(Role.Admin, admin.Role);
    }

    [Fact]
    public void Desativar_o_unico_admin_ativo_lanca_TransicaoInvalida()
    {
        var admin = CriarAdmin();

        Assert.Throws<TransicaoInvalidaException>(() =>
            admin.Desativar(ehUltimoAdminAtivo: true, Agora.AddMinutes(1)));
    }

    [Fact]
    public void Desativar_quando_nao_e_o_ultimo_admin_funciona_e_dispara_evento()
    {
        var admin = CriarAdmin();

        admin.Desativar(ehUltimoAdminAtivo: false, Agora.AddMinutes(1));

        Assert.False(admin.Ativo);
        var evento = Assert.IsType<UsuarioCredenciaisAlteradas>(Assert.Single(admin.DomainEvents));
        Assert.Equal(MotivoAlteracaoCredenciais.Desativado, evento.Motivo);
    }

    [Fact]
    public void TrocarSenha_zera_DeveTrocarSenha_e_dispara_evento()
    {
        var usuario = new Usuario(Guid.NewGuid(), "Admin Semeado", "seed@example.com", "hash-antigo", Role.Admin, true, Agora);

        usuario.TrocarSenha("hash-novo", Agora.AddMinutes(1));

        Assert.False(usuario.DeveTrocarSenha);
        Assert.Equal("hash-novo", usuario.SenhaHash);
        var evento = Assert.IsType<UsuarioCredenciaisAlteradas>(Assert.Single(usuario.DomainEvents));
        Assert.Equal(MotivoAlteracaoCredenciais.SenhaTrocada, evento.Motivo);
    }

    [Fact]
    public void ClearDomainEvents_limpa_a_lista()
    {
        var usuario = CriarAdmin();
        usuario.TrocarSenha("novo-hash", Agora.AddMinutes(1));

        usuario.ClearDomainEvents();

        Assert.Empty(usuario.DomainEvents);
    }
}
