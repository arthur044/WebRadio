using WebRadio.Domain.Enums;
using WebRadio.Domain.Usuarios;
using Xunit;

namespace WebRadio.Domain.Tests.Usuarios;

/// <summary>A claim <c>sv</c> do token só vale enquanto bate com esta versão: ela sobe quando a credencial muda.</summary>
public class UsuarioVersaoSegurancaTests
{
    private static readonly DateTime Agora = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    private static Usuario Locutor() => new(Guid.NewGuid(), "Bia", "bia@example.com", "hash", Role.Locutor, false, Agora);

    [Fact]
    public void Nasce_em_zero()
        => Assert.Equal(0, Locutor().VersaoSeguranca);

    [Fact]
    public void Mudar_o_papel_desativar_e_trocar_a_senha_incrementam_uma_vez_cada()
    {
        var u = Locutor();

        u.AlterarRole(Role.Admin, ehUltimoAdminAtivo: false, Agora);
        Assert.Equal(1, u.VersaoSeguranca);

        u.TrocarSenha("hash-2", Agora);
        Assert.Equal(2, u.VersaoSeguranca);

        u.Desativar(ehUltimoAdminAtivo: false, Agora);
        Assert.Equal(3, u.VersaoSeguranca);
    }

    [Fact]
    public void No_op_e_rehash_de_login_nao_derrubam_os_tokens()
    {
        var u = Locutor();

        u.AlterarRole(Role.Locutor, ehUltimoAdminAtivo: false, Agora); // mesmo papel
        u.AtualizarHashSenha("hash-com-parametros-novos", Agora);       // mesma senha, hash novo
        u.Reativar(Agora);                                              // já está ativo

        Assert.Equal(0, u.VersaoSeguranca);
        Assert.Empty(u.DomainEvents);
    }

    [Fact]
    public void Operacao_recusada_nao_incrementa()
    {
        var admin = new Usuario(Guid.NewGuid(), "Ana", "ana@example.com", "hash", Role.Admin, false, Agora);

        Assert.ThrowsAny<Exception>(() => admin.Desativar(ehUltimoAdminAtivo: true, Agora));
        Assert.ThrowsAny<Exception>(() => admin.AlterarRole(Role.Locutor, ehUltimoAdminAtivo: true, Agora));

        Assert.Equal(0, admin.VersaoSeguranca);
        Assert.True(admin.Ativo);
    }

    [Fact]
    public void Reativar_devolve_o_login_sem_mexer_na_versao_nem_emitir_evento_de_credencial()
    {
        var u = Locutor();
        u.Desativar(ehUltimoAdminAtivo: false, Agora);
        u.ClearDomainEvents();

        u.Reativar(Agora.AddMinutes(5));

        Assert.True(u.Ativo);
        Assert.Equal(1, u.VersaoSeguranca); // a desativação já invalidou os tokens antigos
        Assert.Equal(Agora.AddMinutes(5), u.AtualizadoEmUtc);
        Assert.Empty(u.DomainEvents);
        Assert.Throws<ArgumentException>(() => u.Reativar(DateTime.Now));
    }
}
