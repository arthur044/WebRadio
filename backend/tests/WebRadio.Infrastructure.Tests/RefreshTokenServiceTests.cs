using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Features.Auth;
using WebRadio.Domain.Erros;
using WebRadio.Infrastructure.Seguranca;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>E1-F07b contra SQL Server de verdade: rotação atômica, janela de 10 s, reuso e revogação.</summary>
public class RefreshTokenServiceTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private static async Task<(Cenario C, Guid UsuarioId, string Token)> LogadoAsync(BancoDeTeste banco)
    {
        var c = new Cenario(banco);
        var email = Cenario.Email();
        var usuario = await c.CriarUsuarioAsync(email);
        var login = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, Cenario.IpA), default);
        return (c, usuario.Id, login.Refresh.Token);
    }

    [SqlFact]
    public async Task Rotacao_devolve_um_token_novo_e_o_antigo_depois_da_janela_revoga_a_familia()
    {
        var (c, _, token) = await LogadoAsync(banco);

        var primeiro = await c.NovoRefresh().RotacionarAsync(token, Cenario.IpA, default);
        Assert.True(primeiro.Sucesso);
        Assert.NotEqual(token, primeiro.Novo!.Token);

        // Fora da janela de 10 s: reuso do token já rotacionado = roubo; a família inteira cai.
        c.Relogio.Avancar(TimeSpan.FromSeconds(11));
        Assert.False((await c.NovoRefresh().RotacionarAsync(token, Cenario.IpA, default)).Sucesso);
        Assert.False((await c.NovoRefresh().RotacionarAsync(primeiro.Novo.Token, Cenario.IpA, default)).Sucesso);
    }

    [SqlFact]
    public async Task Reuso_dentro_da_janela_devolve_o_mesmo_sucessor_e_ele_continua_utilizavel()
    {
        var (c, _, token) = await LogadoAsync(banco);

        var a = await c.NovoRefresh().RotacionarAsync(token, Cenario.IpA, default);
        c.Relogio.Avancar(TimeSpan.FromSeconds(5));
        var b = await c.NovoRefresh().RotacionarAsync(token, Cenario.IpA, default);

        Assert.True(a.Sucesso && b.Sucesso);
        Assert.Equal(a.Novo!.Token, b.Novo!.Token);
        Assert.Equal(a.Novo.Hash, b.Novo.Hash);
        Assert.True((await c.NovoRefresh().RotacionarAsync(b.Novo.Token, Cenario.IpA, default)).Sucesso);
    }

    [SqlFact]
    public async Task O_sucessor_deriva_do_pepper_com_HKDF_pepper_diferente_nao_reproduz()
    {
        var (c, _, token) = await LogadoAsync(banco);
        var a = await c.NovoRefresh().RotacionarAsync(token, Cenario.IpA, default);

        // Mesmo pai, outro pepper: o "sucessor" derivado seria outro valor, e o hash não bate com o do banco.
        c.Relogio.Avancar(TimeSpan.FromSeconds(2));
        var outroPepper = await c.NovoRefresh(pepper: new byte[48]).RotacionarAsync(token, Cenario.IpA, default);

        Assert.True(outroPepper.Sucesso);
        Assert.NotEqual(a.Novo!.Token, outroPepper.Novo!.Token);
        Assert.False((await c.NovoRefresh().RotacionarAsync(outroPepper.Novo.Token, Cenario.IpA, default)).Sucesso);
    }

    [SqlFact]
    public async Task Duas_rotacoes_concorrentes_criam_um_unico_sucessor()
    {
        var (c, usuarioId, token) = await LogadoAsync(banco);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => c.NovoRefresh().RotacionarAsync(token, Cenario.IpA, default))));

        Assert.All(resultados, r => Assert.True(r.Sucesso));
        Assert.Single(resultados.Select(r => r.Novo!.Token).Distinct());

        await using var db = c.NovoDb();
        Assert.Equal(2, await db.RefreshTokens.CountAsync(t => t.UsuarioId == usuarioId));          // login + 1 sucessor
        Assert.Equal(1, await db.RefreshTokens.CountAsync(t => t.UsuarioId == usuarioId && t.RevogadoEmUtc == null));
    }

    [SqlFact]
    public async Task Perder_a_corrida_para_outra_aba_e_Concorrente_e_nao_derruba_a_sessao()
    {
        var (c, _, t0) = await LogadoAsync(banco);

        // Aba A rotaciona duas vezes dentro da janela (T0 -> T1 -> T2); a aba B ainda apresenta T0.
        var t1 = await c.NovoRefresh().RotacionarAsync(t0, Cenario.IpA, default);
        var t2 = await c.NovoRefresh().RotacionarAsync(t1.Novo!.Token, Cenario.IpA, default);
        var abaB = await c.NovoRefresh().RotacionarAsync(t0, Cenario.IpA, default);

        Assert.False(abaB.Sucesso);
        Assert.True(abaB.Concorrente);
        await Assert.ThrowsAsync<RefreshConcorrenteException>(
            () => c.NovoRefreshHandler().Handle(new RefreshCommand(t0, Cenario.IpA), default));

        // A família continua viva: o T2 da aba A segue utilizável.
        Assert.True((await c.NovoRefresh().RotacionarAsync(t2.Novo!.Token, Cenario.IpA, default)).Sucesso);
    }

    [SqlFact]
    public async Task Sucessor_revogado_por_logout_dentro_da_janela_NAO_e_corrida_e_a_sessao_esta_morta()
    {
        var (c, _, t0) = await LogadoAsync(banco);
        var t1 = await c.NovoRefresh().RotacionarAsync(t0, Cenario.IpA, default);

        // Logout revoga a família (T1 fica revogado SEM filho) e o T0 chega de uma aba atrasada, ainda dentro dos 10 s.
        await new LogoutHandler(c.NovoRefresh()).Handle(new LogoutCommand(t1.Novo!.Token), default);
        c.Relogio.Avancar(TimeSpan.FromSeconds(2));
        var atrasada = await c.NovoRefresh().RotacionarAsync(t0, Cenario.IpA, default);

        Assert.False(atrasada.Sucesso);
        Assert.False(atrasada.Concorrente); // 401 /erros/credenciais (apaga o cookie), não "tente de novo"
    }

    [SqlFact]
    public async Task Token_expirado_desconhecido_ou_mal_formado_e_invalido_sem_revogar_nada()
    {
        var (c, _, token) = await LogadoAsync(banco);

        Assert.False((await c.NovoRefresh().RotacionarAsync("nao-e-um-token", Cenario.IpA, default)).Sucesso);
        Assert.False((await c.NovoRefresh().RotacionarAsync(Cenario.TokenAleatorio(), Cenario.IpA, default)).Sucesso);

        c.Relogio.Avancar(TimeSpan.FromDays(8));
        var expirado = await c.NovoRefresh().RotacionarAsync(token, Cenario.IpA, default);
        Assert.False(expirado.Sucesso);
        Assert.False(expirado.Concorrente);
    }

    [SqlFact]
    public async Task Desativar_o_usuario_faz_o_refresh_seguinte_falhar_e_derruba_as_sessoes()
    {
        var (c, usuarioId, token) = await LogadoAsync(banco);
        await using (var db = c.NovoDb())
        {
            var alvo = await db.Usuarios.SingleAsync(u => u.Id == usuarioId);
            alvo.Desativar(ehUltimoAdminAtivo: false, c.Relogio.UtcNow);
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<CredenciaisInvalidasException>(
            () => c.NovoRefreshHandler().Handle(new RefreshCommand(token, Cenario.IpA), default));

        await using var conferir = c.NovoDb();
        Assert.Equal(0, await conferir.RefreshTokens.CountAsync(t => t.UsuarioId == usuarioId && t.RevogadoEmUtc == null));
    }

    [SqlFact]
    public async Task Logout_revoga_a_familia_e_token_desconhecido_e_no_op()
    {
        var (c, _, token) = await LogadoAsync(banco);

        await new LogoutHandler(c.NovoRefresh()).Handle(new LogoutCommand(token), default);
        await new LogoutHandler(c.NovoRefresh()).Handle(new LogoutCommand(Cenario.TokenAleatorio()), default);
        await new LogoutHandler(c.NovoRefresh()).Handle(new LogoutCommand(null), default);

        Assert.False((await c.NovoRefresh().RotacionarAsync(token, Cenario.IpA, default)).Sucesso);
    }

    [SqlFact]
    public async Task Trocar_senha_revoga_as_outras_familias_mantem_a_atual_e_tira_a_restricao()
    {
        var c = new Cenario(banco);
        var email = Cenario.Email();
        var usuario = await c.CriarUsuarioAsync(email, deveTrocarSenha: true);
        var atual = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, Cenario.IpA), default);
        var outra = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, Cenario.IpB), default);

        await c.NovoTrocarSenha().Handle(
            new TrocarSenhaCommand(usuario.Id, Cenario.Senha, "uma-senha-nova-forte-9", atual.Refresh.Token, Cenario.IpA), default);

        Assert.False((await c.NovoRefresh().RotacionarAsync(outra.Refresh.Token, Cenario.IpB, default)).Sucesso);
        var depois = await c.NovoRefreshHandler().Handle(new RefreshCommand(atual.Refresh.Token, Cenario.IpA), default);
        Assert.False(depois.DeveTrocarSenha);
        await c.NovoLogin().Handle(new LoginCommand(email, "uma-senha-nova-forte-9", Cenario.IpA), default);
    }

    [SqlFact]
    public async Task Trocar_senha_nao_e_oraculo_5_erros_bloqueiam_ate_a_senha_certa_e_outro_IP_nao()
    {
        var c = new Cenario(banco);
        var usuario = await c.CriarUsuarioAsync(Cenario.Email());
        var erro = typeof(FluentValidation.ValidationException);

        for (var i = 0; i < MemoryLoginThrottle.FalhasParaBloquear; i++)
        {
            await Assert.ThrowsAsync(erro, () => c.NovoTrocarSenha().Handle(
                new TrocarSenhaCommand(usuario.Id, "senha-errada-1234", "uma-senha-nova-forte-9", null, Cenario.IpA), default));
        }

        // Bloqueado: a senha CERTA também é recusada, com a mesma resposta de "senha incorreta".
        await Assert.ThrowsAsync(erro, () => c.NovoTrocarSenha().Handle(
            new TrocarSenhaCommand(usuario.Id, Cenario.Senha, "uma-senha-nova-forte-9", null, Cenario.IpA), default));

        await c.NovoTrocarSenha().Handle(
            new TrocarSenhaCommand(usuario.Id, Cenario.Senha, "uma-senha-nova-forte-9", null, Cenario.IpB), default);
    }
}
