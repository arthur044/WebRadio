using System.Net;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebRadio.Application.Autenticacao;
using WebRadio.Application.Comum;
using WebRadio.Domain.Enums;
using WebRadio.Infrastructure.Opcoes;
using WebRadio.Infrastructure.Seed;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>E1-F07 contra SQL Server de verdade: login, bloqueio por (conta, IP), refresh rotativo e troca de senha.</summary>
public class FluxoDeAutenticacaoTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private static readonly IPAddress IpA = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress IpB = IPAddress.Parse("203.0.113.11");

    private static string Email() => $"user-{Guid.NewGuid():N}@example.com";

    [SqlFact]
    public async Task Login_correto_devolve_access_refresh_e_usuario()
    {
        var c = new Cenario(banco);
        var email = Email();
        var usuario = await c.CriarUsuarioAsync(email, Role.Admin, deveTrocarSenha: true);

        var sessao = await c.NovoLogin().Handle(new LoginCommand(email.ToUpperInvariant(), Cenario.Senha, IpA), default);

        Assert.NotEmpty(sessao.Resposta.AccessToken);
        Assert.NotEmpty(sessao.Refresh.TokenBruto);
        Assert.True(sessao.Resposta.DeveTrocarSenha);
        Assert.Equal(usuario.Id, sessao.Resposta.Usuario.Id);
        Assert.Equal("Admin", sessao.Resposta.Usuario.Role);
    }

    [SqlFact]
    public async Task Senha_errada_email_inexistente_e_conta_inativa_dao_o_mesmo_erro()
    {
        var c = new Cenario(banco);
        var email = Email();
        var inativo = Email();
        await c.CriarUsuarioAsync(email);
        var u = await c.CriarUsuarioAsync(inativo);
        await using (var db = c.NovoDb())
        {
            var alvo = await db.Usuarios.SingleAsync(x => x.Id == u.Id);
            alvo.Desativar(ehUltimoAdminAtivo: false, c.Relogio.UtcAgora);
            await db.SaveChangesAsync();
        }

        var erros = new List<Exception>();
        foreach (var (e, s) in new[] { (email, "senha-errada-1234"), (Email(), Cenario.Senha), (inativo, Cenario.Senha) })
        {
            erros.Add(await Assert.ThrowsAsync<CredenciaisInvalidasException>(
                () => c.NovoLogin().Handle(new LoginCommand(e, s, IpA), default)));
        }

        Assert.Single(erros.Select(e => e.Message).Distinct());
    }

    [SqlFact]
    public async Task Quinta_falha_do_mesmo_IP_bloqueia_e_outro_IP_ainda_loga()
    {
        var c = new Cenario(banco);
        var email = Email();
        await c.CriarUsuarioAsync(email);

        for (var i = 0; i < LoginBloqueio.MaximoFalhas; i++)
        {
            await Assert.ThrowsAsync<CredenciaisInvalidasException>(
                () => c.NovoLogin().Handle(new LoginCommand(email, "senha-errada-1234", IpA), default));
        }

        // Bloqueado: nem a senha certa entra, e o erro é o mesmo 401 genérico (nunca 423).
        await Assert.ThrowsAsync<CredenciaisInvalidasException>(
            () => c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, IpA), default));

        var deOutroIp = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, IpB), default);
        Assert.NotEmpty(deOutroIp.Refresh.TokenBruto);
    }

    [SqlFact]
    public async Task Login_correto_zera_o_contador_de_falhas()
    {
        var c = new Cenario(banco);
        var email = Email();
        await c.CriarUsuarioAsync(email);

        for (var i = 0; i < LoginBloqueio.MaximoFalhas - 1; i++)
        {
            await Assert.ThrowsAsync<CredenciaisInvalidasException>(
                () => c.NovoLogin().Handle(new LoginCommand(email, "senha-errada-1234", IpA), default));
        }

        await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, IpA), default);

        await Assert.ThrowsAsync<CredenciaisInvalidasException>(
            () => c.NovoLogin().Handle(new LoginCommand(email, "senha-errada-1234", IpA), default));
        await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, IpA), default);
    }

    [SqlFact]
    public async Task Redis_fora_do_ar_nao_derruba_o_login()
    {
        var c = new Cenario(banco);
        var email = Email();
        await c.CriarUsuarioAsync(email);
        c.Throttle.Indisponivel = true;

        var sessao = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, IpA), default);

        Assert.NotEmpty(sessao.Resposta.AccessToken);
    }

    [SqlFact]
    public async Task Refresh_rotaciona_e_o_token_antigo_depois_da_janela_revoga_a_familia()
    {
        var c = new Cenario(banco);
        var email = Email();
        await c.CriarUsuarioAsync(email);
        var login = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, IpA), default);

        var primeiro = await c.NovoRefreshHandler().Handle(new RefreshCommand(login.Refresh.TokenBruto, IpA), default);
        Assert.NotEqual(login.Refresh.TokenBruto, primeiro.Refresh.TokenBruto);

        // Fora da janela de 10 s: reuso do token já rotacionado = roubo; a família inteira cai.
        c.Relogio.Avancar(TimeSpan.FromSeconds(11));
        await Assert.ThrowsAsync<CredenciaisInvalidasException>(
            () => c.NovoRefreshHandler().Handle(new RefreshCommand(login.Refresh.TokenBruto, IpA), default));
        await Assert.ThrowsAsync<CredenciaisInvalidasException>(
            () => c.NovoRefreshHandler().Handle(new RefreshCommand(primeiro.Refresh.TokenBruto, IpA), default));
    }

    [SqlFact]
    public async Task Reuso_dentro_da_janela_de_10s_devolve_o_mesmo_sucessor()
    {
        var c = new Cenario(banco);
        var email = Email();
        await c.CriarUsuarioAsync(email);
        var login = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, IpA), default);

        var a = await c.NovoRefresh().RotacionarAsync(login.Refresh.TokenBruto, null);
        c.Relogio.Avancar(TimeSpan.FromSeconds(5));
        var b = await c.NovoRefresh().RotacionarAsync(login.Refresh.TokenBruto, null);

        Assert.True(a.Sucesso);
        Assert.True(b.Sucesso);
        Assert.Equal(a.Novo!.TokenBruto, b.Novo!.TokenBruto);

        // O sucessor devolvido continua utilizável.
        Assert.True((await c.NovoRefresh().RotacionarAsync(b.Novo.TokenBruto, null)).Sucesso);
    }

    [SqlFact]
    public async Task Duas_rotacoes_concorrentes_criam_um_unico_sucessor()
    {
        var c = new Cenario(banco);
        var email = Email();
        var usuario = await c.CriarUsuarioAsync(email);
        var login = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, IpA), default);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => c.NovoRefresh().RotacionarAsync(login.Refresh.TokenBruto, null))));

        Assert.All(resultados, r => Assert.True(r.Sucesso));
        Assert.Single(resultados.Select(r => r.Novo!.TokenBruto).Distinct());

        await using var db = c.NovoDb();
        // Login + exatamente um sucessor, apesar das 4 rotações simultâneas.
        Assert.Equal(2, await db.RefreshTokens.CountAsync(t => t.UsuarioId == usuario.Id));
        Assert.Equal(1, await db.RefreshTokens.CountAsync(t => t.UsuarioId == usuario.Id && t.RevogadoEmUtc == null));
    }

    [SqlFact]
    public async Task Refresh_expirado_ou_desconhecido_e_invalido_sem_revogar_nada()
    {
        var c = new Cenario(banco);
        var email = Email();
        await c.CriarUsuarioAsync(email);
        var login = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, IpA), default);

        Assert.False((await c.NovoRefresh().RotacionarAsync("nao-e-um-token", null)).Sucesso);
        Assert.False((await c.NovoRefresh().RotacionarAsync(Segredos.ChaveBase64Url(), null)).Sucesso);

        c.Relogio.Avancar(TimeSpan.FromDays(8));
        Assert.False((await c.NovoRefresh().RotacionarAsync(login.Refresh.TokenBruto, null)).Sucesso);
    }

    [SqlFact]
    public async Task Desativar_o_usuario_faz_o_refresh_seguinte_falhar()
    {
        var c = new Cenario(banco);
        var email = Email();
        var usuario = await c.CriarUsuarioAsync(email);
        var login = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, IpA), default);

        await using (var db = c.NovoDb())
        {
            var alvo = await db.Usuarios.SingleAsync(x => x.Id == usuario.Id);
            alvo.Desativar(ehUltimoAdminAtivo: false, c.Relogio.UtcAgora);
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<CredenciaisInvalidasException>(
            () => c.NovoRefreshHandler().Handle(new RefreshCommand(login.Refresh.TokenBruto, IpA), default));
    }

    [SqlFact]
    public async Task Logout_revoga_a_familia()
    {
        var c = new Cenario(banco);
        var email = Email();
        await c.CriarUsuarioAsync(email);
        var login = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, IpA), default);

        await new LogoutHandler(c.NovoRefresh()).Handle(new LogoutCommand(login.Refresh.TokenBruto), default);

        Assert.False((await c.NovoRefresh().RotacionarAsync(login.Refresh.TokenBruto, null)).Sucesso);
    }

    [SqlFact]
    public async Task Trocar_senha_revoga_as_outras_familias_mantem_a_atual_e_tira_a_restricao()
    {
        var c = new Cenario(banco);
        var email = Email();
        var usuario = await c.CriarUsuarioAsync(email, deveTrocarSenha: true);
        var atual = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, IpA), default);
        var outra = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, IpB), default);

        var db = c.NovoDb();
        var handler = new TrocarSenhaHandler(db, c.Hasher, c.NovoRefresh(db), c.Relogio, c.Identidade, c.Bloqueio);
        await handler.Handle(new TrocarSenhaCommand(usuario.Id, Cenario.Senha, "uma-senha-nova-forte-9", atual.Refresh.TokenBruto, IpA), default);

        Assert.False((await c.NovoRefresh().RotacionarAsync(outra.Refresh.TokenBruto, null)).Sucesso);
        var depois = await c.NovoRefreshHandler().Handle(new RefreshCommand(atual.Refresh.TokenBruto, IpA), default);
        Assert.False(depois.Resposta.DeveTrocarSenha);
        await c.NovoLogin().Handle(new LoginCommand(email, "uma-senha-nova-forte-9", IpA), default);
    }

    [SqlFact]
    public async Task Trocar_senha_com_senha_atual_errada_e_erro_de_validacao()
    {
        var c = new Cenario(banco);
        var email = Email();
        var usuario = await c.CriarUsuarioAsync(email);

        var db = c.NovoDb();
        var handler = new TrocarSenhaHandler(db, c.Hasher, c.NovoRefresh(db), c.Relogio, c.Identidade, c.Bloqueio);
        var erro = await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(
            new TrocarSenhaCommand(usuario.Id, "senha-atual-errada-1", "uma-senha-nova-forte-9", null, IpA), default));

        Assert.Contains(erro.Errors, f => f.PropertyName == nameof(TrocarSenhaCommand.SenhaAtual));
    }

    [SqlFact]
    public async Task Registrar_cria_Ouvinte_e_recusa_email_repetido()
    {
        var c = new Cenario(banco);
        var email = Email();

        var criado = await new RegistrarHandler(c.NovoDb(), c.Hasher, c.Relogio)
            .Handle(new RegistrarCommand("Maria", email, "uma-senha-forte-123"), default);

        Assert.Equal("Ouvinte", criado.Role);
        Assert.False(criado.DeveTrocarSenha);
        await Assert.ThrowsAsync<EmailJaCadastradoException>(() => new RegistrarHandler(c.NovoDb(), c.Hasher, c.Relogio)
            .Handle(new RegistrarCommand("Outra", email.ToUpperInvariant(), "uma-senha-forte-123"), default));
    }

    [SqlFact]
    public async Task AdminSeeder_cria_um_Admin_com_DeveTrocarSenha_so_se_nao_existir_nenhum()
    {
        var c = new Cenario(banco);
        await using (var limpar = c.NovoDb())
        {
            await limpar.Database.ExecuteSqlRawAsync("DELETE FROM seg.RefreshToken; DELETE FROM seg.Usuario WHERE Role = 1");
        }

        var opcoes = Options.Create(new SeedOptions { AdminEmail = "root@example.com", AdminSenha = "senha-do-admin-123" });
        AdminSeeder Novo() => new(c.NovoDb(), c.Hasher, c.Relogio, opcoes, NullLogger<AdminSeeder>.Instance);

        await Task.WhenAll(Novo().ExecutarAsync(), Novo().ExecutarAsync(), Novo().ExecutarAsync());

        await using var db = c.NovoDb();
        var admins = await db.Usuarios.Where(u => u.Role == Role.Admin).ToListAsync();
        var admin = Assert.Single(admins);
        Assert.True(admin.DeveTrocarSenha);
        Assert.True(c.Hasher.Verificar(admin.SenhaHash, "senha-do-admin-123"));
    }

    [SqlFact]
    public async Task AdminSeeder_sem_configuracao_nao_faz_nada()
    {
        var c = new Cenario(banco);
        await using var db = c.NovoDb();
        var antes = await db.Usuarios.CountAsync();

        await new AdminSeeder(c.NovoDb(), c.Hasher, c.Relogio, Options.Create(new SeedOptions()), NullLogger<AdminSeeder>.Instance).ExecutarAsync();

        Assert.Equal(antes, await db.Usuarios.CountAsync());
    }
}
