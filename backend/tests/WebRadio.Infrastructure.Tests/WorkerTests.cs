using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Domain.Autenticacao;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Interacao;
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
        // provar que ele não se repete.
        Assert.True(await Espera.Ate(() => log.Linhas.Length > 0, TimeSpan.FromSeconds(20)));
        await Task.Delay(TimeSpan.FromSeconds(2));
        await w.StopAsync(default);

        Assert.False(w.EhLider);
        var avisos = log.Linhas.Where(l => l.Nivel >= Microsoft.Extensions.Logging.LogLevel.Warning).ToArray();
        Assert.Single(avisos);
        Assert.Contains("SQL", avisos[0].Texto);
        Assert.DoesNotContain("Password", avisos[0].Texto);
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
            return new ResultadoDaManutencao(0, 0, 0);
        }
    }

    private readonly string _pasta = Directory.CreateTempSubdirectory("webradio-worker-").FullName;
    private readonly LiderancaFalsa _lideranca = new();
    private readonly ManutencaoFalsa _manutencao = new();
    private readonly RelogioFalso _relogio = new(new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc));

    private string Heartbeat => Path.Combine(_pasta, "heartbeat");

    public void Dispose()
    {
        Directory.Delete(_pasta, recursive: true);
        GC.SuppressFinalize(this);
    }

    private CicloDoWorker Novo(string? arquivo = null)
    {
        var servicos = new ServiceCollection().AddScoped<IManutencao>(_ => _manutencao).BuildServiceProvider();
        var opcoes = new CicloOptions { Intervalo = TimeSpan.FromMilliseconds(40), ArquivoDeHeartbeat = arquivo ?? Heartbeat };
        return new CicloDoWorker(_lideranca, servicos.GetRequiredService<IServiceScopeFactory>(), _relogio, opcoes, NullLogger<CicloDoWorker>.Instance);
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
    public async Task Falha_da_manutencao_nao_derruba_o_laco_e_ela_e_tentada_de_novo()
    {
        _lideranca.Lider = true;
        _manutencao.Falhar = true;
        using var ciclo = Novo();
        await ciclo.StartAsync(default);

        Assert.True(await Espera.Ate(() => _manutencao.Execucoes >= 2, TimeSpan.FromSeconds(10)));
        _manutencao.Falhar = false;
        var antes = _manutencao.Execucoes;
        Assert.True(await Espera.Ate(() => _manutencao.Execucoes > antes, TimeSpan.FromSeconds(10)));
        var depois = ciclo.Ciclos;
        Assert.True(await Espera.Ate(() => ciclo.Ciclos >= depois + 5, TimeSpan.FromSeconds(10)));
        await ciclo.StopAsync(default);

        Assert.InRange(_manutencao.Execucoes, antes + 1, antes + 2); // deu certo: para de repetir
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
    }
}
