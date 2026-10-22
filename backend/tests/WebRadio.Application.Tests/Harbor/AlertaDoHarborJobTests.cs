using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Harbor;
using WebRadio.Application.Tests.Auth;
using WebRadio.Application.Tests.Transmissao;
using WebRadio.Infrastructure.Harbor;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using Xunit;

namespace WebRadio.Application.Tests.Harbor;

/// <summary>O laço do AlertaDoHarborJob com a fila de verdade (os outros testes esvaziam a fila à mão).</summary>
public class AlertaDoHarborJobTests
{
    private static readonly TimeSpan Espaco = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan Prazo = TimeSpan.FromSeconds(15);

    private readonly string _banco = Guid.NewGuid().ToString();
    private readonly ConsultasFalsas _consultas = new();
    private readonly FilaDeAlertasDoHarbor _fila = new(new LogCapturado<FilaDeAlertasDoHarbor>());
    private readonly LogCapturado<AlertaDoHarborJob> _logJob = new();
    private readonly ConcurrentQueue<(string Tipo, TimeSpan Quando)> _gravados = new();
    private readonly Stopwatch _relogio = Stopwatch.StartNew();
    private readonly Guid _conhecido = Guid.NewGuid();
    private int _escoposCriados;
    private int _escoposQueFalham;

    public AlertaDoHarborJobTests() => _consultas.Donos[_conhecido] = new DonoDaCredencial(Guid.NewGuid(), "Ana", Ativa: true);

    /// <summary>Registra o instante em que cada evento é gravado, sem mudar o que o OutboxEf faz.</summary>
    private sealed class OutboxRegistrador(RadioDbContext db, ConcurrentQueue<(string, TimeSpan)> gravados, Stopwatch relogio) : IOutbox
    {
        private readonly OutboxEf _real = new(db);
        public void EnfileirarParaTodos(string tipo, object carga) => _real.EnfileirarParaTodos(tipo, carga);
        public void EnfileirarSessoesRevogadas(Guid usuarioId) => _real.EnfileirarSessoesRevogadas(usuarioId);
        public void EnfileirarParaGrupos(IReadOnlyCollection<string> grupos, string tipo, object carga)
        {
            _real.EnfileirarParaGrupos(grupos, tipo, carga);
            gravados.Enqueue((System.Text.Json.JsonSerializer.Serialize(carga, OutboxEf.Json).Contains("\"tipo\":\"global\"") ? "global" : "credencial", relogio.Elapsed));
        }
    }

    private sealed class FabricaDeEscopos(IServiceProvider raiz, Func<bool> falhar) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => falhar() ? throw new InvalidOperationException("sem escopo (simulado)") : raiz.CreateScope();
    }

    private AlertaDoHarborJob NovoJob()
    {
        var servicos = new ServiceCollection()
            .AddScoped(_ => AuthFixture.NovoDb(_banco))
            .AddScoped<IRadioDbContext>(sp => sp.GetRequiredService<RadioDbContext>())
            .AddScoped<IOutbox>(sp => new OutboxRegistrador(sp.GetRequiredService<RadioDbContext>(), _gravados, _relogio))
            .AddSingleton<IConsultasDoHarbor>(_consultas)
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<AlertaDoHarbor>>(new LogCapturado<AlertaDoHarbor>())
            .AddScoped<AlertaDoHarbor>()
            .BuildServiceProvider();
        var fabrica = new FabricaDeEscopos(servicos, () =>
        {
            Interlocked.Increment(ref _escoposCriados);
            return _escoposQueFalham > 0 && Interlocked.Decrement(ref _escoposQueFalham) >= 0;
        });
        return new AlertaDoHarborJob(_fila, fabrica, _logJob) { EspacoEntreConsultas = Espaco };
    }

    private static async Task Esperar(Func<bool> condicao, string o_que)
    {
        var limite = Stopwatch.StartNew();
        while (!condicao())
        {
            Assert.True(limite.Elapsed < Prazo, $"não aconteceu a tempo: {o_que}");
            await Task.Delay(10);
        }
    }

    private AlertaPendente Credencial() => new(AlertasDaFalha.Identificador, _conhecido);

    private AlertaPendente Global() => new(AlertasDaFalha.Global, Guid.NewGuid());

    [Fact]
    public async Task Alerta_que_lanca_nao_derruba_o_laco_o_seguinte_e_gravado()
    {
        _escoposQueFalham = 1; // a criação do escopo do 1º item lança (fora do try interno do AlertaDoHarbor)
        var job = NovoJob();
        await job.StartAsync(default);

        _fila.Enfileirar(Credencial());
        await Esperar(() => _escoposCriados >= 1, "o 1º item ser tentado");
        _fila.Enfileirar(Credencial());
        await Esperar(() => !_gravados.IsEmpty, "o 2º item ser gravado");

        Assert.Single(_gravados);
        Assert.Contains(_logJob.Linhas, l => l.Contains("HARBOR_ALERTA_FALHOU"));
        await job.StopAsync(default);
    }

    [Fact]
    public async Task O_espaco_vem_depois_da_consulta_do_dono_e_o_global_nao_espera_por_ele()
    {
        var job = NovoJob();
        await job.StartAsync(default);

        _fila.Enfileirar(Credencial());
        _fila.Enfileirar(Credencial()); // só sai depois do espaço
        _fila.Enfileirar(Global());     // laço próprio: não espera o espaço das credenciais
        await Esperar(() => _gravados.Count >= 3, "os três alertas serem gravados");

        var porTipo = _gravados.OrderBy(g => g.Quando).ToArray();
        var credenciais = porTipo.Where(g => g.Tipo == "credencial").ToArray();
        var global = porTipo.Single(g => g.Tipo == "global");
        Assert.True(credenciais[1].Quando - credenciais[0].Quando >= Espaco - TimeSpan.FromMilliseconds(100),
            $"a 2ª consulta veio {(credenciais[1].Quando - credenciais[0].Quando).TotalMilliseconds:0} ms depois da 1ª");
        Assert.True(global.Quando < credenciais[1].Quando, "o global esperou atrás do espaço das credenciais");
        await job.StopAsync(default);
    }

    [Fact]
    public async Task O_laco_sai_no_cancelamento_mesmo_esperando_o_espaco_ou_a_fila()
    {
        var job = NovoJob();
        await job.StartAsync(default);
        _fila.Enfileirar(Credencial());
        await Esperar(() => !_gravados.IsEmpty, "o 1º item ser gravado"); // agora o laço de credenciais está no espaço

        await job.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(job.ExecuteTask!.IsCompletedSuccessfully);
    }
}
