using System.Net;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Application.Autenticacao;
using WebRadio.Application.Comum;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

public class RedisForaDoArTests
{
    [Fact]
    public async Task Depois_da_primeira_falha_do_Redis_as_demais_chamadas_do_login_nem_tentam()
    {
        var throttle = new ThrottleEmMemoria { Indisponivel = true };
        var bloqueio = new LoginBloqueio(throttle, NullLogger<LoginBloqueio>.Instance);

        Assert.False(await bloqueio.EstaBloqueadoAsync("A@B", null, default));
        Assert.Equal(TimeSpan.Zero, await bloqueio.RegistrarFalhaAsync("A@B", null, default));
        await bloqueio.LimparAsync("A@B", null, default);

        // Sem o curto-circuito seriam 4+ chamadas (cada uma com o timeout do Redis); com ele, so a que descobriu a queda.
        Assert.Equal(1, throttle.Chamadas);
    }
}

public class FluxoDaRevisaoTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private static readonly IPAddress Ip = IPAddress.Parse("203.0.113.50");

    private static string Email() => $"rev-{Guid.NewGuid():N}@example.com";

    [SqlFact]
    public async Task Trocar_senha_nao_e_oraculo_5_erros_bloqueiam_ate_a_senha_certa()
    {
        var c = new Cenario(banco);
        var usuario = await c.CriarUsuarioAsync(Email());

        TrocarSenhaHandler Novo()
        {
            var db = c.NovoDb();
            return new TrocarSenhaHandler(db, c.Hasher, c.NovoRefresh(db), c.Relogio, c.Identidade, c.Bloqueio);
        }

        for (var i = 0; i < LoginBloqueio.MaximoFalhas; i++)
        {
            await Assert.ThrowsAsync<ValidationException>(() => Novo().Handle(
                new TrocarSenhaCommand(usuario.Id, "senha-errada-1234", "uma-senha-nova-forte-9", null, Ip), default));
        }

        // Bloqueado: a senha CERTA tambem e recusada, com a mesma resposta de "senha incorreta".
        await Assert.ThrowsAsync<ValidationException>(() => Novo().Handle(
            new TrocarSenhaCommand(usuario.Id, Cenario.Senha, "uma-senha-nova-forte-9", null, Ip), default));

        // Outro IP nao esta bloqueado.
        await Novo().Handle(new TrocarSenhaCommand(usuario.Id, Cenario.Senha, "uma-senha-nova-forte-9", null, IPAddress.Parse("203.0.113.51")), default);
    }

    [SqlFact]
    public async Task Refresh_que_perde_a_corrida_para_outra_aba_e_Concorrente_e_nao_derruba_a_sessao()
    {
        var c = new Cenario(banco);
        var email = Email();
        await c.CriarUsuarioAsync(email);
        var login = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, Ip), default);

        // Aba A rotaciona duas vezes dentro da janela (T0 -> T1 -> T2); a aba B ainda apresenta T0.
        var t1 = await c.NovoRefresh().RotacionarAsync(login.Refresh.TokenBruto, null);
        var t2 = await c.NovoRefresh().RotacionarAsync(t1.Novo!.TokenBruto, null);
        var aba_b = await c.NovoRefresh().RotacionarAsync(login.Refresh.TokenBruto, null);

        Assert.False(aba_b.Sucesso);
        Assert.True(aba_b.Concorrente);
        await Assert.ThrowsAsync<RefreshConcorrenteException>(
            () => c.NovoRefreshHandler().Handle(new RefreshCommand(login.Refresh.TokenBruto, Ip), default));

        // A familia continua viva: o T2 da aba A segue utilizavel.
        Assert.True((await c.NovoRefresh().RotacionarAsync(t2.Novo!.TokenBruto, null)).Sucesso);
    }
}
