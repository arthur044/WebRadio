using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Domain.Autenticacao;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Interacao;
using WebRadio.Domain.Transmissao;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.DependencyInjection;
using WebRadio.Infrastructure.Persistencia.Outbox;
using WebRadio.Infrastructure.Tests.Suporte;
using WebRadio.Infrastructure.Trabalhos;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

internal static class Espera
{
    public static async Task<bool> Ate(Func<bool> condicao, TimeSpan limite)
    {
        var fim = DateTime.UtcNow + limite;
        while (DateTime.UtcNow < fim)
        {
            if (condicao()) return true;
            await Task.Delay(50);
        }

        return condicao();
    }
}

/// <summary>E1-F11: eleição de líder contra SQL Server de verdade (sp_getapplock de sessão não existe no provedor em memória).</summary>
public class LiderancaDoWorkerTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private static readonly LiderancaOptions Rapido = new() { Intervalo = TimeSpan.FromMilliseconds(300) };

    private LiderancaDoWorker Novo() => new(banco.ConnectionString, Rapido, NullLogger<LiderancaDoWorker>.Instance);

    [SqlFact]
    public async Task Dois_workers_so_um_vira_lider_e_matar_o_lider_faz_o_outro_assumir()
    {
        using var a = Novo();
        using var b = Novo();
        await a.StartAsync(default);
        await b.StartAsync(default);

        Assert.True(await Espera.Ate(() => a.EhLider || b.EhLider, TimeSpan.FromSeconds(15)));
        for (var i = 0; i < 10; i++)
        {
            Assert.False(a.EhLider && b.EhLider); // vários ciclos: nunca os dois
            await Task.Delay(150);
        }

        var (lider, outro) = a.EhLider ? (a, b) : (b, a);
        await lider.StopAsync(default); // fecha a conexão dedicada: o applock de sessão morre com ela

        Assert.False(lider.EhLider);
        Assert.True(await Espera.Ate(() => outro.EhLider, TimeSpan.FromSeconds(30)));
        await outro.StopAsync(default);
    }

    [SqlFact]
    public async Task Lider_que_perde_a_sessao_no_SQL_deixa_de_ser_lider_e_volta_a_disputar()
    {
        using var a = Novo();
        await a.StartAsync(default);
        Assert.True(await Espera.Ate(() => a.EhLider, TimeSpan.FromSeconds(15)));

        // Derruba a sessão do líder pelo lado do servidor (queda de rede, failover): o lock some sem o worker pedir.
        await using (var db = banco.CriarContexto())
        {
            var sessoes = await db.Database
                .SqlQuery<short>($"SELECT session_id AS Value FROM sys.dm_exec_sessions WHERE program_name = {LiderancaDoWorker.NomeDaAplicacao} AND database_id = DB_ID()")
                .ToListAsync();
            Assert.Single(sessoes);
            // KILL não aceita parâmetro: o número (smallint vindo do próprio servidor) entra por SQL dinâmico.
            await db.Database.ExecuteSqlAsync($"DECLARE @matar nvarchar(20) = CONCAT(N'KILL ', {sessoes[0]}); EXEC (@matar);");
        }

        // Primeiro a perda: logo após o KILL o "a" ainda se acha líder (só percebe no próximo ciclo) e o "b" ainda nem
        // existe, então "a ^ b" já seria verdadeiro e o teste passaria sem provar a reconexão.
        Assert.True(await Espera.Ate(() => !a.EhLider, TimeSpan.FromSeconds(15)));

        using var b = Novo();
        await b.StartAsync(default);
        // Alguém reassume (a que reconectou ou a nova)...
        Assert.True(await Espera.Ate(() => a.EhLider || b.EhLider, TimeSpan.FromSeconds(30)));
        // ...e, ao longo de vários ciclos, nunca as duas (como no primeiro teste).
        for (var i = 0; i < 10; i++)
        {
            Assert.False(a.EhLider && b.EhLider);
            await Task.Delay(150);
        }

        await a.StopAsync(default);
        await b.StopAsync(default);
    }
}

/// <summary>Guarda as mensagens para o teste contar quantas vezes o worker se queixou.</summary>
internal sealed class LoggerDeTeste<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly List<(Microsoft.Extensions.Logging.LogLevel Nivel, string Texto)> _linhas = [];

    public (Microsoft.Extensions.Logging.LogLevel Nivel, string Texto)[] Linhas { get { lock (_linhas) return [.. _linhas]; } }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_linhas) _linhas.Add((logLevel, formatter(state, exception)));
    }
}

/// <summary>E1-F11, sem SQL Server: o worker que nunca falou com o banco precisa dizer isso no log (review do #29, M1).</summary>
public class LiderancaSemBancoTests
{
    [Fact]
    public async Task Nao_lider_que_nao_alcanca_o_banco_avisa_uma_vez_e_nao_a_cada_ciclo()
    {
        var log = new LoggerDeTeste<LiderancaDoWorker>();
        // Porta fechada: o connect falha na hora com SqlException, e o worker nunca chega a ser líder.
        using var w = new LiderancaDoWorker("Server=127.0.0.1,1;User Id=u;Password=p;Connect Timeout=1;ConnectRetryCount=0;Encrypt=False",
            new LiderancaOptions { Intervalo = TimeSpan.FromMilliseconds(100) }, log);

        await w.StartAsync(default);
        // Primeiro o aviso (a 1ª tentativa de conexão pode demorar mais num runner lento); depois ciclos de sobra para
        // provar que ele não se repete. O contador de tentativas (e não um sleep) garante que houve outros ciclos de
        // falha DEPOIS do aviso: num runner lento um sleep passaria sem provar a não-repetição (B3-05).
        Assert.True(await Espera.Ate(() => log.Linhas.Length > 0, TimeSpan.FromSeconds(20)));
        var depoisDoAviso = w.Tentativas;
        Assert.True(await Espera.Ate(() => w.Tentativas >= depoisDoAviso + 3, TimeSpan.FromSeconds(30)));
        await w.StopAsync(default);

        Assert.False(w.EhLider);
        var avisos = log.Linhas.Where(l => l.Nivel >= Microsoft.Extensions.Logging.LogLevel.Warning).ToArray();
        Assert.Single(avisos);
        Assert.Contains("SQL", avisos[0].Texto);
        Assert.DoesNotContain("Password", avisos[0].Texto);
    }
}

public class LoteDaManutencaoTests
{
    [Fact]
    public void O_lote_padrao_fica_abaixo_do_limiar_de_escalonamento_de_lock_do_SQL_Server()
    {
        // O SQL Server tenta escalar para lock de tabela a partir de ~5.000 locks num statement, e um DELETE TOP (N) toca o
        // clustered e os índices: com N = 5.000 o limiar é ultrapassado (B3-14). A seção 11 do 03 cita o mesmo número.
        Assert.InRange(Manutencao.LotePadrao, 1, 4000);
    }
}

/// <summary>E1-F11: rotinas da seção 11 do 03-schema-sqlserver.sql.</summary>
public class ManutencaoTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private static byte[] Hash() => RandomNumberGenerator.GetBytes(32);

    private async Task<Usuario> UsuarioAsync()
    {
        await using var db = banco.CriarContexto();
        var u = new Usuario(Guid.CreateVersion7(), "Ana", Cenario.Email(), "hash", Role.Ouvinte, false, DateTime.UtcNow);
        db.Usuarios.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    private async Task<ResultadoDaManutencao> ExecutarAsync(int lote = Manutencao.LotePadrao)
    {
        await using var db = banco.CriarContexto();
        return await new Manutencao(db) { Lote = lote }.ExecutarAsync(default);
    }

    [SqlFact]
    public async Task Anonimiza_os_hashes_do_ouvinte_depois_de_30_dias_e_preserva_os_recentes()
    {
        var antigo = new PedidoMusica(Guid.CreateVersion7(), null, null, "Zé", "Faixa", "Banda", null, Hash(), Hash(), DateTime.UtcNow.AddDays(-31));
        var recente = new PedidoMusica(Guid.CreateVersion7(), null, null, "Zé", "Faixa", "Banda", null, Hash(), Hash(), DateTime.UtcNow.AddDays(-29));
        await using (var db = banco.CriarContexto())
        {
            db.PedidosMusica.AddRange(antigo, recente);
            await db.SaveChangesAsync();
        }

        var r = await ExecutarAsync();

        Assert.Equal(1, r.PedidosAnonimizados);
        await using var leitura = banco.CriarContexto();
        var a = await leitura.PedidosMusica.AsNoTracking().SingleAsync(p => p.Id == antigo.Id);
        Assert.Null(a.ListenerDeviceHash);
        Assert.Null(a.ListenerIpHash);
        Assert.Equal("Faixa", a.TituloMusica); // o pedido em si fica
        var n = await leitura.PedidosMusica.AsNoTracking().SingleAsync(p => p.Id == recente.Id);
        Assert.NotNull(n.ListenerDeviceHash);
        Assert.NotNull(n.ListenerIpHash);

        Assert.Equal(0, (await ExecutarAsync()).PedidosAnonimizados); // idempotente
    }

    [SqlFact]
    public async Task Apaga_outbox_processado_ha_mais_de_7_dias_e_nunca_o_pendente()
    {
        var antigo = new EventoOutbox("TocandoAgora", "{}");
        var recente = new EventoOutbox("TocandoAgora", "{}");
        var pendente = new EventoOutbox("TocandoAgora", "{}");
        await using (var db = banco.CriarContexto())
        {
            db.EventosOutbox.AddRange(antigo, recente, pendente);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE infra.EventoOutbox SET CriadoEmUtc = DATEADD(DAY, -9, SYSUTCDATETIME()), ProcessadoEmUtc = DATEADD(DAY, -8, SYSUTCDATETIME()) WHERE Id = {antigo.Id}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE infra.EventoOutbox SET ProcessadoEmUtc = DATEADD(DAY, -6, SYSUTCDATETIME()) WHERE Id = {recente.Id}");
            // Parado há meses sem ter sido entregue: fica para análise (01 §2.8), a limpeza não o toca.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE infra.EventoOutbox SET CriadoEmUtc = DATEADD(DAY, -90, SYSUTCDATETIME()) WHERE Id = {pendente.Id}");
        }

        var r = await ExecutarAsync();

        Assert.Equal(1, r.EventosApagados);
        await using var leitura = banco.CriarContexto();
        var restantes = await leitura.EventosOutbox.AsNoTracking().Where(e => e.Id >= antigo.Id).Select(e => e.Id).ToListAsync();
        Assert.DoesNotContain(antigo.Id, restantes);
        Assert.Contains(recente.Id, restantes);
        Assert.Contains(pendente.Id, restantes);
    }

    [SqlFact]
    public async Task Apaga_refresh_vencido_ha_mais_de_1_dia_inclusive_a_cadeia_de_rotacao_e_preserva_o_resto()
    {
        var u = await UsuarioAsync();
        var familia = Guid.NewGuid();
        // Cadeia rotacionada inteira vencida: pai → filho → neto (FK_RefreshToken_SubstituidoPor é RESTRICT).
        var pai = new RefreshToken(Guid.CreateVersion7(), u.Id, Hash(), familia, DateTime.UtcNow.AddDays(-12), TimeSpan.FromDays(7), null);
        var filho = new RefreshToken(Guid.CreateVersion7(), u.Id, Hash(), familia, DateTime.UtcNow.AddDays(-11), TimeSpan.FromDays(7), null);
        var neto = new RefreshToken(Guid.CreateVersion7(), u.Id, Hash(), familia, DateTime.UtcNow.AddDays(-10), TimeSpan.FromDays(7), null);
        // Vencido há menos de 1 dia (carência) e válido: ficam.
        var naCarencia = new RefreshToken(Guid.CreateVersion7(), u.Id, Hash(), Guid.NewGuid(), DateTime.UtcNow.AddDays(-7).AddHours(-12), TimeSpan.FromDays(7), null);
        var valido = new RefreshToken(Guid.CreateVersion7(), u.Id, Hash(), Guid.NewGuid(), DateTime.UtcNow, TimeSpan.FromDays(7), null);
        await using (var db = banco.CriarContexto())
        {
            db.RefreshTokens.AddRange(pai, filho, neto, naCarencia, valido);
            await db.SaveChangesAsync();
            pai.MarcarSubstituidoPor(filho.Id, DateTime.UtcNow.AddDays(-11));
            filho.MarcarSubstituidoPor(neto.Id, DateTime.UtcNow.AddDays(-10));
            await db.SaveChangesAsync();
        }

        // Lote 1 força vários passes: a ordem de exclusão dentro da cadeia não pode esbarrar na FK.
        var r = await ExecutarAsync(lote: 1);

        Assert.Equal(3, r.RefreshApagados);
        await using var leitura = banco.CriarContexto();
        var restantes = await leitura.RefreshTokens.AsNoTracking().Where(t => t.UsuarioId == u.Id).Select(t => t.Id).ToListAsync();
        Assert.Equal(new[] { naCarencia.Id, valido.Id }.Order(), restantes.Order());
    }

    [SqlFact]
    public async Task Apaga_credencial_de_transmissao_revogada_ha_mais_de_90_dias_e_preserva_a_ativa_e_a_recente()
    {
        var u = await UsuarioAsync();
        CredencialTransmissao Nova() => new(Guid.NewGuid(), u.Id, Guid.NewGuid(), Hash(), RandomNumberGenerator.GetBytes(16), DateTime.UtcNow.AddDays(-200));
        var velhaA = Nova();
        var velhaB = Nova();
        var velhaC = Nova();
        var recente = Nova();
        var ativa = Nova();
        // Só uma ativa por locutor (UX_CredencialTransmissao_Ativa): as outras já nascem revogadas.
        velhaA.Revogar(DateTime.UtcNow.AddDays(-91));
        velhaB.Revogar(DateTime.UtcNow.AddDays(-91));
        velhaC.Revogar(DateTime.UtcNow.AddDays(-120));
        recente.Revogar(DateTime.UtcNow.AddDays(-89));
        await using (var db = banco.CriarContexto())
        {
            db.CredenciaisTransmissao.AddRange(velhaA, velhaB, velhaC, recente, ativa);
            await db.SaveChangesAsync();
        }

        // Lote 2 com 3 candidatas: o laço roda de novo até o lote vir incompleto.
        var r = await ExecutarAsync(lote: 2);

        Assert.Equal(3, r.CredenciaisApagadas);
        await using var leitura = banco.CriarContexto();
        var restantes = await leitura.CredenciaisTransmissao.AsNoTracking().Where(c => c.UsuarioId == u.Id).Select(c => c.Id).ToListAsync();
        Assert.Equal(new[] { recente.Id, ativa.Id }.Order(), restantes.Order());
        Assert.Equal(0, (await ExecutarAsync()).CredenciaisApagadas); // idempotente
    }

    [SqlFact]
    public async Task Refresh_vencido_ainda_apontado_por_um_token_que_nao_venceu_fica_para_o_proximo_dia_sem_erro()
    {
        var u = await UsuarioAsync();
        var familia = Guid.NewGuid();
        // Caso raro (duração do refresh mudou entre as emissões): o sucessor vence antes do antecessor.
        var antecessor = new RefreshToken(Guid.CreateVersion7(), u.Id, Hash(), familia, DateTime.UtcNow.AddDays(-5), TimeSpan.FromDays(30), null);
        var sucessor = new RefreshToken(Guid.CreateVersion7(), u.Id, Hash(), familia, DateTime.UtcNow.AddDays(-4), TimeSpan.FromDays(2), null);
        await using (var db = banco.CriarContexto())
        {
            db.RefreshTokens.AddRange(antecessor, sucessor);
            await db.SaveChangesAsync();
            antecessor.MarcarSubstituidoPor(sucessor.Id, DateTime.UtcNow.AddDays(-4));
            await db.SaveChangesAsync();
        }

        var r = await ExecutarAsync();

        Assert.Equal(0, r.RefreshApagados);
        await using var leitura = banco.CriarContexto();
        Assert.Equal(2, await leitura.RefreshTokens.CountAsync(t => t.UsuarioId == u.Id));
    }
}

/// <summary>E1-F11: laço de 10 s. Sem banco: a liderança e a manutenção são dublês.</summary>
public class CicloDoWorkerTests : IDisposable
{
    private sealed class LiderancaFalsa : ILideranca
    {
        public volatile bool Lider;
        public bool EhLider => Lider;
    }

    private sealed class ManutencaoFalsa : IManutencao
    {
        private int _execucoes;

        public int Execucoes => Volatile.Read(ref _execucoes);
        public bool Falhar { get; set; }
        public TaskCompletionSource? Segurar { get; set; }

        public async Task<ResultadoDaManutencao> ExecutarAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _execucoes);
            if (Segurar is { } segurar) await segurar.Task.WaitAsync(ct);
            if (Falhar) throw new InvalidOperationException("banco fora do ar (simulado)");
            return new ResultadoDaManutencao(0, 0, 0, 0);
        }
    }

    private sealed class TransicoesFalsas : ITransicoesDaGrade
    {
        private int _execucoes;

        public int Execucoes => Volatile.Read(ref _execucoes);
        public volatile bool Falhar;
        public TaskCompletionSource? Segurar { get; set; }

        public async Task<ResultadoDasTransicoes> ExecutarAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _execucoes);
            if (Segurar is { } segurar) await segurar.Task.WaitAsync(ct);
            if (Falhar) throw new InvalidOperationException("banco fora do ar (simulado)");
            return new ResultadoDasTransicoes(0, 0);
        }
    }

    private sealed class ExpiracaoFalsa : IExpiracaoDePedidos
    {
        private int _execucoes;

        public int Execucoes => Volatile.Read(ref _execucoes);
        public volatile bool Falhar;

        public Task<int> ExecutarAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _execucoes);
            return Falhar ? throw new InvalidOperationException("banco fora do ar (simulado)") : Task.FromResult(0);
        }
    }

    private readonly string _pasta = Directory.CreateTempSubdirectory("webradio-worker-").FullName;
    private readonly LiderancaFalsa _lideranca = new();
    private readonly ManutencaoFalsa _manutencao = new();
    private readonly TransicoesFalsas _transicoes = new();
    private readonly ExpiracaoFalsa _expiracao = new();
    private readonly RelogioFalso _relogio = new(new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc));

    private string Heartbeat => Path.Combine(_pasta, "heartbeat");

    public void Dispose()
    {
        Directory.Delete(_pasta, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static readonly TimeSpan RecuoInicial = TimeSpan.FromMinutes(5);

    private CicloDoWorker Novo(string? arquivo = null, Microsoft.Extensions.Logging.ILogger<CicloDoWorker>? log = null)
    {
        var servicos = new ServiceCollection().AddScoped<IManutencao>(_ => _manutencao).AddScoped<ITransicoesDaGrade>(_ => _transicoes)
            .AddScoped<IExpiracaoDePedidos>(_ => _expiracao).BuildServiceProvider();
        var opcoes = new CicloOptions
        {
            Intervalo = TimeSpan.FromMilliseconds(40),
            ArquivoDeHeartbeat = arquivo ?? Heartbeat,
            RecuoInicialDaManutencao = RecuoInicial,
            RecuoMaximoDaManutencao = TimeSpan.FromHours(1),
        };
        return new CicloDoWorker(_lideranca, servicos.GetRequiredService<IServiceScopeFactory>(), _relogio, opcoes, log ?? NullLogger<CicloDoWorker>.Instance);
    }

    [Fact]
    public async Task Lider_roda_a_expiracao_dos_pedidos_a_cada_60_segundos_e_quem_nao_e_lider_nao()
    {
        using var ciclo = Novo();
        await ciclo.StartAsync(default);

        Assert.True(await Espera.Ate(() => ciclo.Ciclos >= 4, TimeSpan.FromSeconds(10)));
        Assert.Equal(0, _expiracao.Execucoes); // não é líder

        _lideranca.Lider = true;
        Assert.True(await Espera.Ate(() => _expiracao.Execucoes == 1, TimeSpan.FromSeconds(10)));
        var ciclos = ciclo.Ciclos;
        Assert.True(await Espera.Ate(() => ciclo.Ciclos >= ciclos + 5, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, _expiracao.Execucoes); // ainda não passaram 60 s

        _relogio.Avancar(TimeSpan.FromSeconds(61));
        Assert.True(await Espera.Ate(() => _expiracao.Execucoes == 2, TimeSpan.FromSeconds(10)));
        await ciclo.StopAsync(default);
    }

    [Fact]
    public async Task Falha_na_expiracao_dos_pedidos_nao_derruba_o_laco_e_tenta_de_novo_no_proximo_intervalo()
    {
        _expiracao.Falhar = true;
        _lideranca.Lider = true;
        using var ciclo = Novo();
        await ciclo.StartAsync(default);

        Assert.True(await Espera.Ate(() => _expiracao.Execucoes == 1, TimeSpan.FromSeconds(10)));
        var ciclos = ciclo.Ciclos;
        Assert.True(await Espera.Ate(() => ciclo.Ciclos >= ciclos + 5, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, _expiracao.Execucoes); // sem recuo de 10 s: só no próximo intervalo de 60 s

        _expiracao.Falhar = false;
        _relogio.Avancar(TimeSpan.FromSeconds(61));
        Assert.True(await Espera.Ate(() => _expiracao.Execucoes == 2, TimeSpan.FromSeconds(10)));
        await ciclo.StopAsync(default);
    }

    [Fact]
    public async Task Worker_que_nao_e_lider_toca_o_heartbeat_a_cada_tick_e_nao_roda_job()
    {
        using var ciclo = Novo();
        await ciclo.StartAsync(default);

        Assert.True(await Espera.Ate(() => ciclo.Ciclos >= 3, TimeSpan.FromSeconds(10)));
        var primeiro = File.GetLastWriteTimeUtc(Heartbeat);
        var antes = ciclo.Ciclos;
        Assert.True(await Espera.Ate(() => ciclo.Ciclos >= antes + 3 && File.GetLastWriteTimeUtc(Heartbeat) > primeiro, TimeSpan.FromSeconds(10)));
        await ciclo.StopAsync(default);

        Assert.Equal(0, _manutencao.Execucoes);
    }

    [Fact]
    public async Task Lider_roda_a_manutencao_uma_vez_por_dia()
    {
        _lideranca.Lider = true;
        using var ciclo = Novo();
        await ciclo.StartAsync(default);

        Assert.True(await Espera.Ate(() => _manutencao.Execucoes == 1, TimeSpan.FromSeconds(10)));
        var antes = ciclo.Ciclos;
        Assert.True(await Espera.Ate(() => ciclo.Ciclos >= antes + 5, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, _manutencao.Execucoes); // mesmo dia: não repete

        _relogio.Avancar(TimeSpan.FromHours(24));
        Assert.True(await Espera.Ate(() => _manutencao.Execucoes == 2, TimeSpan.FromSeconds(10)));
        await ciclo.StopAsync(default);
    }

    [Fact]
    public async Task Job_demorado_nao_segura_o_heartbeat_nem_e_disparado_em_dobro()
    {
        _lideranca.Lider = true;
        _manutencao.Segurar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ciclo = Novo();
        await ciclo.StartAsync(default);

        Assert.True(await Espera.Ate(() => _manutencao.Execucoes == 1, TimeSpan.FromSeconds(10)));
        var antes = ciclo.Ciclos;
        var marca = File.GetLastWriteTimeUtc(Heartbeat);
        // O job continua preso e os ticks seguem: o healthcheck não pode acusar worker morto por causa de um lote longo.
        Assert.True(await Espera.Ate(() => ciclo.Ciclos >= antes + 5 && File.GetLastWriteTimeUtc(Heartbeat) > marca, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, _manutencao.Execucoes);

        _manutencao.Segurar.SetResult();
        await ciclo.StopAsync(default);
    }

    [Fact]
    public async Task Falha_da_manutencao_nao_derruba_o_laco_e_a_nova_tentativa_respeita_o_recuo()
    {
        _lideranca.Lider = true;
        _manutencao.Falhar = true;
        using var ciclo = Novo();
        await ciclo.StartAsync(default);

        // A falha só conta depois que o ciclo registrou o recuo: avançar o relógio antes disso mudaria o instante da próxima tentativa.
        Assert.True(await Espera.Ate(() => ciclo.FalhasSeguidasDaManutencao == 1, TimeSpan.FromSeconds(10)));
        // Sem recuo (B3-07) seriam tentativas a cada ciclo: o relógio não andou, então fica em UMA, com vários ciclos de sobra.
        var antes = ciclo.Ciclos;
        Assert.True(await Espera.Ate(() => ciclo.Ciclos >= antes + 5, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, _manutencao.Execucoes);

        // Passado o recuo inicial tenta de novo; a falha seguinte dobra o recuo.
        _relogio.Avancar(RecuoInicial);
        Assert.True(await Espera.Ate(() => ciclo.FalhasSeguidasDaManutencao == 2, TimeSpan.FromSeconds(10)));
        Assert.Equal(2, _manutencao.Execucoes);
        _relogio.Avancar(RecuoInicial);
        var ciclos = ciclo.Ciclos;
        Assert.True(await Espera.Ate(() => ciclo.Ciclos >= ciclos + 5, TimeSpan.FromSeconds(10)));
        Assert.Equal(2, _manutencao.Execucoes); // 1× o recuo inicial já não basta: o recuo agora é 2×

        // Banco de volta: a próxima tentativa (depois do recuo dobrado) dá certo e a manutenção volta ao ritmo diário.
        _manutencao.Falhar = false;
        _relogio.Avancar(RecuoInicial);
        Assert.True(await Espera.Ate(() => _manutencao.Execucoes == 3 && ciclo.FalhasSeguidasDaManutencao == 0, TimeSpan.FromSeconds(10)));
        var depois = ciclo.Ciclos;
        Assert.True(await Espera.Ate(() => ciclo.Ciclos >= depois + 5, TimeSpan.FromSeconds(10)));
        await ciclo.StopAsync(default);

        Assert.Equal(3, _manutencao.Execucoes); // deu certo: para de repetir
    }

    [Fact]
    public async Task Sequencia_de_falhas_da_manutencao_gera_um_unico_erro_e_o_retorno_e_registrado()
    {
        _lideranca.Lider = true;
        _manutencao.Falhar = true;
        var log = new LoggerDeTeste<CicloDoWorker>();
        using var ciclo = Novo(log: log);
        await ciclo.StartAsync(default);

        for (var tentativa = 1; tentativa <= 3; tentativa++)
        {
            // Espera a falha ser registrada (e o recuo calculado) antes de avançar o relógio.
            Assert.True(await Espera.Ate(() => ciclo.FalhasSeguidasDaManutencao == tentativa, TimeSpan.FromSeconds(10)));
            _relogio.Avancar(TimeSpan.FromHours(2)); // acima de qualquer recuo
        }

        Assert.True(await Espera.Ate(() => ciclo.FalhasSeguidasDaManutencao == 4, TimeSpan.FromSeconds(10)));
        _manutencao.Falhar = false;
        _relogio.Avancar(TimeSpan.FromHours(2));
        Assert.True(await Espera.Ate(() => _manutencao.Execucoes == 5, TimeSpan.FromSeconds(10)));
        Assert.True(await Espera.Ate(() => log.Linhas.Any(l => l.Texto.Contains("WORKER_MANUTENCAO") && l.Nivel == Microsoft.Extensions.Logging.LogLevel.Information), TimeSpan.FromSeconds(10)));
        await ciclo.StopAsync(default);

        // 4 falhas seguidas = 1 erro (com a stack); as demais saem sem repetir o erro.
        Assert.Single(log.Linhas, l => l.Nivel >= Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task Lider_roda_as_transicoes_da_grade_a_cada_tick_e_quem_nao_e_lider_nao()
    {
        using var semLider = Novo();
        await semLider.StartAsync(default);
        Assert.True(await Espera.Ate(() => semLider.Ciclos >= 5, TimeSpan.FromSeconds(10)));
        await semLider.StopAsync(default);
        Assert.Equal(0, _transicoes.Execucoes);

        _lideranca.Lider = true;
        using var ciclo = Novo();
        await ciclo.StartAsync(default);
        Assert.True(await Espera.Ate(() => _transicoes.Execucoes >= 4, TimeSpan.FromSeconds(10)));
        await ciclo.StopAsync(default);
    }

    [Fact]
    public async Task Transicao_demorada_nao_segura_o_heartbeat_nem_roda_em_dobro()
    {
        _lideranca.Lider = true;
        _transicoes.Segurar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ciclo = Novo();
        await ciclo.StartAsync(default);
        Assert.True(await Espera.Ate(() => _transicoes.Execucoes == 1, TimeSpan.FromSeconds(10)));
        var antes = ciclo.Ciclos;
        var marca = File.GetLastWriteTimeUtc(Heartbeat);

        Assert.True(await Espera.Ate(() => ciclo.Ciclos >= antes + 5 && File.GetLastWriteTimeUtc(Heartbeat) > marca, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, _transicoes.Execucoes); // a primeira ainda não terminou: não há segunda em paralelo

        _transicoes.Segurar.SetResult();
        await ciclo.StopAsync(default);
    }

    [Fact]
    public async Task Falha_nas_transicoes_nao_derruba_o_laco_gera_um_unico_erro_e_o_retorno_e_registrado()
    {
        _lideranca.Lider = true;
        _transicoes.Falhar = true;
        var log = new LoggerDeTeste<CicloDoWorker>();
        using var ciclo = Novo(log: log);
        await ciclo.StartAsync(default);

        // Tenta de novo a cada ciclo (são só dois UPDATEs baratos), mas o log não se repete: um erro por sequência de falhas.
        Assert.True(await Espera.Ate(() => _transicoes.Execucoes >= 5, TimeSpan.FromSeconds(10)));
        Assert.Single(log.Linhas, l => l.Nivel >= Microsoft.Extensions.Logging.LogLevel.Error && l.Texto.Contains("WORKER_TRANSICOES"));

        _transicoes.Falhar = false;
        Assert.True(await Espera.Ate(() => log.Linhas.Any(l => l.Nivel == Microsoft.Extensions.Logging.LogLevel.Information && l.Texto.Contains("WORKER_TRANSICOES")), TimeSpan.FromSeconds(10)));
        await ciclo.StopAsync(default);
        Assert.Single(log.Linhas, l => l.Nivel >= Microsoft.Extensions.Logging.LogLevel.Error && l.Texto.Contains("WORKER_TRANSICOES"));
    }

    [Fact]
    public async Task Heartbeat_que_nao_pode_ser_gravado_nao_derruba_o_laco()
    {
        using var ciclo = Novo(Path.Combine(_pasta, "pasta-que-nao-existe", "heartbeat"));
        await ciclo.StartAsync(default);

        Assert.True(await Espera.Ate(() => ciclo.Ciclos >= 3, TimeSpan.FromSeconds(10)));
        await ciclo.StopAsync(default);
    }

    [Fact]
    public async Task Parar_o_host_cancela_o_job_em_andamento()
    {
        _lideranca.Lider = true;
        _manutencao.Segurar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ciclo = Novo();
        await ciclo.StartAsync(default);
        Assert.True(await Espera.Ate(() => _manutencao.Execucoes == 1, TimeSpan.FromSeconds(10)));

        await ciclo.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
    }
}

public class ConexaoDoBancoTests
{
    private static IConfiguration Config(params (string Chave, string? Valor)[] itens) =>
        new ConfigurationBuilder().AddInMemoryCollection(itens.ToDictionary(i => i.Chave, i => i.Valor)).Build();

    [Fact]
    public void Sem_Banco_Senha_a_connection_string_vale_como_veio()
    {
        var cs = InfrastructureServiceCollectionExtensions.ConnectionStringDoBanco(
            Config(("ConnectionStrings:RadioDb", "Server=x;Database=WebRadio;User Id=u;Password=local")));

        Assert.Equal("Server=x;Database=WebRadio;User Id=u;Password=local", cs);
    }

    [Fact]
    public void Banco_Senha_do_arquivo_de_segredo_entra_na_connection_string_em_memoria()
    {
        var cs = InfrastructureServiceCollectionExtensions.ConnectionStringDoBanco(
            Config(("ConnectionStrings:RadioDb", "Server=sqlserver;Database=WebRadio;User Id=webradio_app"), ("Banco:Senha", "s3nha-Do_Arquivo-com-24-ou-mais")));

        var partes = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
        Assert.Equal("s3nha-Do_Arquivo-com-24-ou-mais", partes.Password);
        Assert.Equal("webradio_app", partes.UserID);
        Assert.Equal("sqlserver", partes.DataSource);
    }

    [Fact]
    public void Banco_Senha_com_valor_de_exemplo_recusa_o_boot_sem_mostrar_o_valor()
    {
        var e = Assert.Throws<InvalidOperationException>(() => InfrastructureServiceCollectionExtensions.ConnectionStringDoBanco(
            Config(("ConnectionStrings:RadioDb", "Server=sqlserver;Database=WebRadio;User Id=webradio_app"), ("Banco:Senha", "__GERAR__-abc"))));

        Assert.Contains("Banco:Senha", e.Message);
        Assert.DoesNotContain("-abc", e.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("curta-demais")]
    [InlineData("CHANGE_ME-CHANGE_ME-CHANGE_ME")]
    public void Banco_Senha_vazia_curta_ou_de_exemplo_recusa_o_boot(string senha)
    {
        // Arquivo de segredo presente mas vazio/curto: erro de configuração que não pode virar "sobe e falha só no login".
        var e = Assert.Throws<InvalidOperationException>(() => InfrastructureServiceCollectionExtensions.ConnectionStringDoBanco(
            Config(("ConnectionStrings:RadioDb", "Server=sqlserver;Database=WebRadio;User Id=webradio_app"), ("Banco:Senha", senha))));

        Assert.Contains("Banco:Senha", e.Message);
        if (!string.IsNullOrWhiteSpace(senha)) Assert.DoesNotContain(senha, e.Message);
    }

    [Fact]
    public void Fora_de_Development_sem_Banco_Senha_recusa_o_boot()
    {
        var e = Assert.Throws<InvalidOperationException>(() => InfrastructureServiceCollectionExtensions.ConnectionStringDoBanco(
            Config(("ConnectionStrings:RadioDb", "Server=sqlserver;Database=WebRadio;User Id=webradio_app;Password=no-compose")),
            exigirSenhaDeSegredo: true));

        Assert.Contains("Banco:Senha", e.Message);
    }

    [Fact]
    public void O_nome_da_aplicacao_do_servico_vai_para_a_connection_string_e_vence_o_da_configuracao()
    {
        var cs = InfrastructureServiceCollectionExtensions.ConnectionStringDoBanco(
            Config(("ConnectionStrings:RadioDb", "Server=sqlserver;Database=WebRadio;User Id=webradio_app;Application Name=outro")),
            nomeDaAplicacao: NomesDeAplicacao.Worker);

        Assert.Equal("WebRadio.Worker", new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs).ApplicationName);
    }

    [Fact]
    public void Os_nomes_da_aplicacao_sao_os_da_tabela_do_guia_de_conexao()
    {
        // docs/conexao-sql-server.md §3: um Application Name por serviço (sys.dm_exec_sessions.program_name).
        Assert.Equal("WebRadio.Api", NomesDeAplicacao.Api);
        Assert.Equal("WebRadio.Worker", NomesDeAplicacao.Worker);
        Assert.Equal("WebRadio.Worker.Lideranca", LiderancaDoWorker.NomeDaAplicacao);
    }

    [Fact]
    public void Sem_connection_string_o_registro_falha_com_mensagem_clara()
    {
        var e = Assert.Throws<InvalidOperationException>(() => InfrastructureServiceCollectionExtensions.ConnectionStringDoBanco(Config()));

        Assert.Contains("RadioDb", e.Message);
    }

    [Fact]
    public void O_Worker_registra_so_banco_relogio_e_trabalhos_sem_emissor_de_tokens_nem_pepper()
    {
        var servicos = new ServiceCollection().AddLogging();
        servicos.AddWorkerInfrastructure(Config(("ConnectionStrings:RadioDb", "Server=x;Database=WebRadio;User Id=u;Password=local")));

        // 04 §Serviços: o worker não recebe JWT nem pepper; nada que dependa deles pode estar no contêiner dele.
        Assert.DoesNotContain(servicos, s => s.ServiceType == typeof(Application.Abstracoes.IEmissorDeTokens));
        Assert.DoesNotContain(servicos, s => s.ServiceType == typeof(Application.Abstracoes.IIpHasher));
        using var provedor = servicos.BuildServiceProvider(validateScopes: true);
        Assert.Equal(2, provedor.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Count());
        using var escopo = provedor.CreateScope();
        Assert.IsType<Manutencao>(escopo.ServiceProvider.GetRequiredService<IManutencao>());
        Assert.IsType<TransicoesDaGrade>(escopo.ServiceProvider.GetRequiredService<ITransicoesDaGrade>());
        Assert.IsType<ExpiracaoDePedidos>(escopo.ServiceProvider.GetRequiredService<IExpiracaoDePedidos>());
    }

    [Theory]
    [InlineData("4", false)]
    [InlineData("0", false)]
    [InlineData("10081", false)]
    [InlineData("abc", false)]
    [InlineData("120", true)]
    public void Pedidos_ExpiracaoMinutos_fora_da_faixa_ou_nao_numerica_recusa_o_boot_do_Worker(string valor, bool aceito)
    {
        var servicos = new ServiceCollection().AddLogging();
        Action registrar = () => servicos.AddWorkerInfrastructure(Config(("ConnectionStrings:RadioDb", "Server=x;Database=WebRadio;User Id=u;Password=local"), ("Pedidos:ExpiracaoMinutos", valor)));

        if (aceito) registrar();
        else Assert.Throws<InvalidOperationException>(registrar);
    }
}
