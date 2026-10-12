using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Auth;
using WebRadio.Application.Features.Usuarios;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Seguranca;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// PATCH /usuarios e a versão de segurança contra SQL Server de verdade: RowVersion, applock 'seg:admins', revogação
/// de refresh e outbox na mesma transação. Cada teste usa um banco só seu quando precisa contar Admins.
/// </summary>
public class GestaoDeUsuariosTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private static async Task<byte[]> VersaoDeAsync(Cenario c, Guid id)
    {
        await using var db = c.NovoDb();
        return (await db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == id)).RowVersion;
    }

    private static async Task<int> OutboxDeAsync(Cenario c, Guid usuarioId)
    {
        await using var db = c.NovoDb();
        var eventos = await db.EventosOutbox.AsNoTracking().Where(e => e.Tipo == EventosDoOutbox.UsuarioSessoesRevogadas).ToListAsync();
        return eventos.Count(e => JsonDocument.Parse(e.Payload).RootElement.GetProperty("usuarioId").GetGuid() == usuarioId);
    }

    [SqlFact]
    public async Task Mudar_o_papel_incrementa_a_sv_revoga_os_refresh_e_grava_o_outbox_na_mesma_transacao()
    {
        var c = new Cenario(banco);
        var email = Cenario.Email();
        var usuario = await c.CriarUsuarioAsync(email);
        var login = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, Cenario.IpA), default);

        var dto = await c.NovoAlterarUsuario().Handle(new AlterarUsuarioCommand(usuario.Id, Role.Ouvinte, null, await VersaoDeAsync(c, usuario.Id)), default);

        Assert.Equal("Ouvinte", dto.Role);
        Assert.NotEqual(Convert.ToBase64String(usuario.RowVersion), dto.Versao); // o ETag devolvido já é o novo
        await using var db = c.NovoDb();
        Assert.Equal(1, (await db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == usuario.Id)).VersaoSeguranca);
        Assert.False((await c.NovoRefresh().RotacionarAsync(login.Refresh.Token, Cenario.IpA, default)).Sucesso);
        Assert.Equal(1, await OutboxDeAsync(c, usuario.Id));
    }

    [SqlFact]
    public async Task RowVersion_antigo_e_recusado_e_nada_muda()
    {
        var c = new Cenario(banco);
        var usuario = await c.CriarUsuarioAsync(Cenario.Email());
        var versaoLida = await VersaoDeAsync(c, usuario.Id);
        await c.NovoAlterarUsuario().Handle(new AlterarUsuarioCommand(usuario.Id, Role.Ouvinte, null, versaoLida), default);

        // Segundo Admin com a tela aberta desde antes: o If-Match dele é o RowVersion antigo.
        await Assert.ThrowsAsync<VersaoDesatualizadaException>(() =>
            c.NovoAlterarUsuario().Handle(new AlterarUsuarioCommand(usuario.Id, null, false, versaoLida), default));

        await using var db = c.NovoDb();
        var atual = await db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == usuario.Id);
        Assert.True(atual.Ativo);
        Assert.Equal(1, atual.VersaoSeguranca);
        Assert.Equal(1, await OutboxDeAsync(c, usuario.Id));
    }

    [SqlFact]
    public async Task Falha_depois_de_revogar_desfaz_a_revogacao_e_o_outbox()
    {
        var c = new Cenario(banco);
        var email = Cenario.Email();
        var usuario = await c.CriarUsuarioAsync(email);
        var login = await c.NovoLogin().Handle(new LoginCommand(email, Cenario.Senha, Cenario.IpA), default);
        var versao = await VersaoDeAsync(c, usuario.Id);

        // O SaveChanges do handler falha (o outbox recusa a carga), depois de o UPDATE de revogação já ter rodado na transação.
        var db = c.NovoDb();
        var handler = new AlterarUsuarioHandler(db, new TransacaoComTrava(db), c.NovoRefresh(db), new OutboxQueFalha(), c.NovaVersaoDeSeguranca(db), c.Relogio);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(new AlterarUsuarioCommand(usuario.Id, Role.Ouvinte, null, versao), default));

        await using var conferencia = c.NovoDb();
        var atual = await conferencia.Usuarios.AsNoTracking().SingleAsync(u => u.Id == usuario.Id);
        Assert.Equal(Role.Locutor, atual.Role);
        Assert.Equal(0, atual.VersaoSeguranca);
        Assert.True((await c.NovoRefresh().RotacionarAsync(login.Refresh.Token, Cenario.IpA, default)).Sucesso);
    }

    private sealed class OutboxQueFalha : IOutbox
    {
        public void Enfileirar(string tipo, object carga) => throw new InvalidOperationException("falha simulada depois da revogação");
    }

    [SqlFact]
    public async Task Dois_Admins_se_desativando_ao_mesmo_tempo_nunca_deixam_o_sistema_sem_Admin()
    {
        // Banco exclusivo: o teste conta os Admins ativos do banco inteiro.
        var proprio = new BancoDeTeste();
        await proprio.InitializeAsync();
        try
        {
            var c = new Cenario(proprio);
            var a = await c.CriarUsuarioAsync(Cenario.Email(), Role.Admin);
            var b = await c.CriarUsuarioAsync(Cenario.Email(), Role.Admin);
            var (va, vb) = (await VersaoDeAsync(c, a.Id), await VersaoDeAsync(c, b.Id));

            // Sem a trava, as duas transações veriam "o outro ainda está ativo" e as duas desativariam.
            var resultados = await Task.WhenAll(
                Tentar(() => c.NovoAlterarUsuario().Handle(new AlterarUsuarioCommand(a.Id, null, false, va), default)),
                Tentar(() => c.NovoAlterarUsuario().Handle(new AlterarUsuarioCommand(b.Id, null, false, vb), default)));

            Assert.Single(resultados, r => r is null);
            Assert.IsType<TransicaoInvalidaException>(Assert.Single(resultados, r => r is not null));
            await using var db = c.NovoDb();
            Assert.Equal(1, await db.Usuarios.CountAsync(u => u.Role == Role.Admin && u.Ativo));
        }
        finally
        {
            await proprio.DisposeAsync();
        }

        static async Task<Exception?> Tentar(Func<Task> acao)
        {
            try { await acao(); return null; }
            catch (Exception e) { return e; }
        }
    }

    [SqlFact]
    public async Task Usuario_inexistente_e_NaoEncontrado()
    {
        var c = new Cenario(banco);
        await Assert.ThrowsAsync<NaoEncontradoException>(() =>
            c.NovoAlterarUsuario().Handle(new AlterarUsuarioCommand(Guid.NewGuid(), null, false, new byte[8]), default));
    }

    [SqlFact]
    public async Task Trocar_a_senha_incrementa_a_sv_e_esquece_o_cache()
    {
        var c = new Cenario(banco);
        var usuario = await c.CriarUsuarioAsync(Cenario.Email());
        Assert.Equal(0, await c.NovaVersaoDeSeguranca().AtualAsync(usuario.Id, default)); // entra no cache

        await c.NovoTrocarSenha().Handle(new TrocarSenhaCommand(usuario.Id, Cenario.Senha, "outra-senha-bem-longa-456", null, Cenario.IpA), default);

        Assert.Equal(1, await c.NovaVersaoDeSeguranca().AtualAsync(usuario.Id, default));
    }

    // ---- cache da versão de segurança ----

    [SqlFact]
    public async Task Versao_fica_em_cache_ate_ser_esquecida_e_usuario_inativo_ou_inexistente_nao_tem_versao()
    {
        var c = new Cenario(banco);
        var usuario = await c.CriarUsuarioAsync(Cenario.Email());
        var versoes = c.NovaVersaoDeSeguranca();
        Assert.Equal(0, await versoes.AtualAsync(usuario.Id, default));

        // Mudança feita por OUTRA instância (sem Esquecer aqui): esta continua vendo o valor em cache (até 15 s).
        await using (var db = c.NovoDb())
        {
            var u = await db.Usuarios.SingleAsync(x => x.Id == usuario.Id);
            u.Desativar(ehUltimoAdminAtivo: false, c.Relogio.UtcNow);
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await c.NovaVersaoDeSeguranca().AtualAsync(usuario.Id, default));

        versoes.Esquecer(usuario.Id);
        Assert.Null(await c.NovaVersaoDeSeguranca().AtualAsync(usuario.Id, default)); // inativo
        Assert.Null(await versoes.AtualAsync(Guid.NewGuid(), default));               // inexistente
    }

    [Fact]
    public void O_cache_da_versao_dura_15_segundos()
        => Assert.Equal(TimeSpan.FromSeconds(15), VersaoDeSeguranca.DuracaoDoCache);

    // ---- desbloqueio de login (memória; o do Redis usa SCAN e fica para o CI com Redis real) ----

    [Fact]
    public async Task Desbloquear_a_conta_limpa_todos_os_IPs_e_o_contador_da_conta_sem_tocar_em_outra_conta()
    {
        var relogio = new RelogioFalso(DateTime.UtcNow);
        var t = new MemoryLoginThrottle(relogio, new IpHasher(new byte[32]), NullLogger<MemoryLoginThrottle>.Instance);
        foreach (var ip in new[] { Cenario.IpA, Cenario.IpB })
            for (var i = 0; i < MemoryLoginThrottle.FalhasParaBloquear; i++)
            {
                await t.RegistrarFalhaAsync("ANA@X.COM", ip, default);
                await t.RegistrarFalhaAsync("ANA@X.COM.BR", ip, default); // prefixo da outra: não pode ser levada junto
            }

        await t.DesbloquearContaAsync("ANA@X.COM", default);

        Assert.False((await t.AvaliarAsync("ANA@X.COM", Cenario.IpA, default)).Bloqueado);
        Assert.False((await t.AvaliarAsync("ANA@X.COM", Cenario.IpB, default)).Bloqueado);
        Assert.True((await t.AvaliarAsync("ANA@X.COM.BR", Cenario.IpA, default)).Bloqueado);
        Assert.Equal(3, t.Entradas); // sobraram os 2 pares e a conta da outra
        await t.DesbloquearContaAsync("NUNCA@FALHOU.COM", default); // no-op
    }

    [Fact]
    public async Task Desbloqueio_com_fallback_limpa_a_memoria_e_o_Redis_e_propaga_a_falha_do_Redis()
    {
        var relogio = new RelogioFalso(DateTime.UtcNow);
        var memoria = new MemoryLoginThrottle(relogio, new IpHasher(new byte[32]), NullLogger<MemoryLoginThrottle>.Instance);
        for (var i = 0; i < MemoryLoginThrottle.FalhasParaBloquear; i++) await memoria.RegistrarFalhaAsync("ANA@X.COM", Cenario.IpA, default);
        var redisQuebrado = new RedisFalso(quebrado: true);
        var comFallback = new LoginThrottleComFallback(redisQuebrado, memoria, relogio, NullLogger<LoginThrottleComFallback>.Instance);

        // Redis fora: o Admin vê o erro (e repete), mas a memória desta instância já foi limpa.
        await Assert.ThrowsAsync<InvalidOperationException>(() => comFallback.DesbloquearContaAsync("ANA@X.COM", default));
        Assert.False((await memoria.AvaliarAsync("ANA@X.COM", Cenario.IpA, default)).Bloqueado);
        Assert.True(comFallback.RedisEmUso); // não abre o disjuntor por causa de uma ação administrativa

        var redisOk = new RedisFalso(quebrado: false);
        await new LoginThrottleComFallback(redisOk, memoria, relogio, NullLogger<LoginThrottleComFallback>.Instance).DesbloquearContaAsync("ANA@X.COM", default);
        Assert.Equal(["ANA@X.COM"], redisOk.Desbloqueadas);
    }

    private sealed class RedisFalso(bool quebrado) : ILoginThrottle
    {
        public List<string> Desbloqueadas { get; } = [];

        public Task<AvaliacaoDeLogin> AvaliarAsync(string e, byte[] i, CancellationToken ct) => Task.FromResult(new AvaliacaoDeLogin(false, TimeSpan.Zero));

        public Task RegistrarFalhaAsync(string e, byte[] i, CancellationToken ct) => Task.CompletedTask;

        public Task LimparAsync(string e, byte[] i, CancellationToken ct) => Task.CompletedTask;

        public Task DesbloquearContaAsync(string e, CancellationToken ct)
        {
            if (quebrado) throw new InvalidOperationException("Redis fora do ar (simulado)");
            Desbloqueadas.Add(e);
            return Task.CompletedTask;
        }
    }
}
