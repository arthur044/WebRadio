using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Application.Features.Transmissao;
using WebRadio.Application.Tests.Auth;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Transmissao;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Persistencia;
using Xunit;

namespace WebRadio.Application.Tests.Transmissao;

/// <summary>06 "Revogação automática": desativar, trocar a senha e ir para Ouvinte revogam; Admin ↔ Locutor não.</summary>
public class RevogarCredencialAoAlterarCredenciaisTests
{
    private static (RadioDbContext Db, Usuario Usuario, CredencialTransmissao Credencial, RevogarCredencialAoAlterarCredenciais Handler) Cenario(Role role = Role.Locutor)
    {
        var db = AuthFixture.NovoDb();
        var usuario = AuthFixture.CriarUsuario(db, new HasherContador(), role: role);
        var g = SegredoDeTransmissao.Gerar();
        var credencial = new CredencialTransmissao(Guid.NewGuid(), usuario.Id, Guid.NewGuid(), g.Hash, g.Salt, AuthFixture.Agora);
        db.CredenciaisTransmissao.Add(credencial);
        db.SaveChanges();
        var handler = new RevogarCredencialAoAlterarCredenciais(db, new RelogioFalso(AuthFixture.Agora.AddMinutes(1)),
            NullLogger<RevogarCredencialAoAlterarCredenciais>.Instance);
        return (db, usuario, credencial, handler);
    }

    [Theory]
    [InlineData(MotivoAlteracaoCredenciais.Desativado)]
    [InlineData(MotivoAlteracaoCredenciais.SenhaTrocada)]
    public async Task Desativar_e_trocar_a_senha_revogam(MotivoAlteracaoCredenciais motivo)
    {
        var (db, usuario, credencial, handler) = Cenario();

        await handler.HandleAsync(new UsuarioCredenciaisAlteradas(usuario.Id, motivo), CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(AuthFixture.Agora.AddMinutes(1), credencial.RevogadaEmUtc);
    }

    [Fact]
    public async Task Ir_para_Ouvinte_revoga()
    {
        var (db, usuario, credencial, handler) = Cenario();
        usuario.AlterarRole(Role.Ouvinte, ehUltimoAdminAtivo: false, AuthFixture.Agora);

        await handler.HandleAsync(new UsuarioCredenciaisAlteradas(usuario.Id, MotivoAlteracaoCredenciais.RoleAlterada), CancellationToken.None);

        Assert.False(credencial.EstaAtiva);
    }

    [Theory]
    [InlineData(Role.Locutor, Role.Admin)]
    [InlineData(Role.Admin, Role.Locutor)]
    public async Task Troca_entre_Admin_e_Locutor_nao_revoga(Role de, Role para)
    {
        var (_, usuario, credencial, handler) = Cenario(de);
        usuario.AlterarRole(para, ehUltimoAdminAtivo: false, AuthFixture.Agora);

        await handler.HandleAsync(new UsuarioCredenciaisAlteradas(usuario.Id, MotivoAlteracaoCredenciais.RoleAlterada), CancellationToken.None);

        Assert.True(credencial.EstaAtiva);
    }

    [Fact]
    public async Task Sem_credencial_ativa_nao_faz_nada()
    {
        var (db, usuario, credencial, handler) = Cenario();
        credencial.Revogar(AuthFixture.Agora);
        await db.SaveChangesAsync();

        await handler.HandleAsync(new UsuarioCredenciaisAlteradas(usuario.Id, MotivoAlteracaoCredenciais.Desativado), CancellationToken.None);

        Assert.Equal(AuthFixture.Agora, credencial.RevogadaEmUtc);
    }
}
