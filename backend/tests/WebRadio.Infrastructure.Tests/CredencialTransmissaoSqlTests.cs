using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Application;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Transmissao;
using WebRadio.Application.Features.Usuarios;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Eventos;
using WebRadio.Infrastructure.Persistencia.Outbox;
using WebRadio.Infrastructure.Seguranca;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>E2-F04 contra SQL Server de verdade: o índice de "uma ativa", a ordem UPDATE → INSERT e a revogação pelo evento.</summary>
public class CredencialTransmissaoSqlTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private readonly Cenario _c = new(banco);

    private GerarCredencialHandler Gerar(RadioDbContext? db = null) =>
        new(db ?? _c.NovoDb(), _c.Hasher, new MemoryRequestThrottle(_c.Relogio), new IpHasher(_c.Pepper), _c.Relogio,
            NullLogger<GerarCredencialHandler>.Instance);

    private async Task<int> AtivasAsync(Guid usuarioId)
    {
        await using var db = _c.NovoDb();
        return await db.CredenciaisTransmissao.CountAsync(c => c.UsuarioId == usuarioId && c.RevogadaEmUtc == null);
    }

    [SqlFact]
    public async Task Gerar_de_novo_revoga_a_anterior_no_mesmo_SaveChanges()
    {
        var u = await _c.CriarUsuarioAsync(Cenario.Email());

        var primeira = await Gerar().Handle(new GerarCredencialCommand(u.Id, Cenario.Senha), CancellationToken.None);
        var segunda = await Gerar().Handle(new GerarCredencialCommand(u.Id, Cenario.Senha), CancellationToken.None);

        await using var db = _c.NovoDb();
        var todas = await db.CredenciaisTransmissao.Where(c => c.UsuarioId == u.Id).ToListAsync();
        Assert.Equal(2, todas.Count);
        Assert.NotNull(todas.Single(c => c.Identificador == primeira.Identificador).RevogadaEmUtc);
        Assert.Null(todas.Single(c => c.Identificador == segunda.Identificador).RevogadaEmUtc);
    }

    [SqlFact]
    public async Task Duas_geracoes_concorrentes_terminam_com_exatamente_uma_ativa_e_sem_erro_inesperado()
    {
        for (var rodada = 0; rodada < 5; rodada++)
        {
            var u = await _c.CriarUsuarioAsync(Cenario.Email());
            var throttle = new MemoryRequestThrottle(_c.Relogio);
            Task<CredencialGeradaDto> Uma() => Task.Run(() => new GerarCredencialHandler(_c.NovoDb(), _c.Hasher, throttle, new IpHasher(_c.Pepper),
                _c.Relogio, NullLogger<GerarCredencialHandler>.Instance).Handle(new GerarCredencialCommand(u.Id, Cenario.Senha), CancellationToken.None));

            var tarefas = new[] { Uma(), Uma() };
            try { await Task.WhenAll(tarefas); } catch { /* conferido abaixo */ }

            Assert.All(tarefas, t => Assert.True(t.IsCompletedSuccessfully || t.Exception?.InnerException is GeracaoConcorrenteException,
                $"falha inesperada: {t.Exception?.InnerException}"));
            Assert.Contains(tarefas, t => t.IsCompletedSuccessfully);
            Assert.Equal(1, await AtivasAsync(u.Id));
        }
    }

    [SqlTheory]
    [InlineData(Role.Ouvinte, null, true)]
    [InlineData(Role.Admin, null, false)]
    [InlineData(null, false, true)]
    public async Task Alterar_o_usuario_revoga_pelo_evento_na_mesma_transacao(Role? role, bool? ativo, bool revoga)
    {
        var u = await _c.CriarUsuarioAsync(Cenario.Email());
        await Gerar().Handle(new GerarCredencialCommand(u.Id, Cenario.Senha), CancellationToken.None);
        // Um Admin a mais: o alvo nunca é o "último Admin ativo".
        await _c.CriarUsuarioAsync(Cenario.Email(), Role.Admin);

        await using var sp = Servicos();
        await using (var escopo = sp.CreateAsyncScope())
        {
            byte[] versao;
            await using (var leitura = _c.NovoDb()) versao = (await leitura.Usuarios.SingleAsync(x => x.Id == u.Id)).RowVersion;
            await escopo.ServiceProvider.GetRequiredService<AlteracaoDeUsuario>().Alterar(new AlterarUsuarioCommand(u.Id, role, ativo, versao));
        }

        Assert.Equal(revoga ? 0 : 1, await AtivasAsync(u.Id));
    }

    /// <summary>A composição real do DbContext (interceptor + handlers da Application) sobre o banco de teste.</summary>
    private ServiceProvider Servicos()
    {
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddApplication();
        s.AddScoped<DespachanteDeEventosDeDominio>();
        s.AddDbContext<RadioDbContext>((escopo, o) => o.UseSqlServer(banco.ConnectionString).ComEventosDeDominio(escopo));
        s.AddScoped<IRadioDbContext>(x => x.GetRequiredService<RadioDbContext>());
        s.AddSingleton<IClock>(_c.Relogio);
        s.AddScoped<AlteracaoDeUsuario>(x =>
        {
            var db = x.GetRequiredService<RadioDbContext>();
            return new AlteracaoDeUsuario(new AlterarUsuarioHandler(db, new TransacaoComTrava(db), _c.NovoRefresh(db), new OutboxEf(db),
                _c.NovaVersaoDeSeguranca(db), _c.Relogio));
        });
        return s.BuildServiceProvider();
    }

    private sealed class AlteracaoDeUsuario(AlterarUsuarioHandler handler)
    {
        public Task Alterar(AlterarUsuarioCommand cmd) => handler.Handle(cmd, CancellationToken.None);
    }
}
