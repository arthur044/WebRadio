using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace WebRadio.Infrastructure.DependencyInjection;

/// <summary>
/// A conexão com o Redis, aberta NO BOOT com <c>ConnectAsync</c> (hosted service), em vez de <c>Connect</c> síncrono no primeiro
/// uso, que travava a primeira requisição de login numa thread do pool. Com <c>AbortOnConnectFail = false</c> o boot não falha
/// se o Redis estiver fora: a Api sobe, o disjuntor do login cai para a memória e o cliente reconecta sozinho. Se alguém pedir o
/// multiplexador antes do boot (testes, ordem de hosted services), cai no <c>Connect</c> síncrono de antes.
/// </summary>
public sealed class ConexaoRedis(IConfiguration configuration) : IHostedService, IAsyncDisposable
{
    private IConnectionMultiplexer? _multiplexador;
    private readonly object _trava = new();

    public IConnectionMultiplexer Multiplexador
    {
        get
        {
            if (_multiplexador is not null) return _multiplexador;
            lock (_trava) return _multiplexador ??= ConnectionMultiplexer.Connect(CriarOpcoes(configuration));
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_multiplexador is not null) return;
        var conectado = await ConnectionMultiplexer.ConnectAsync(CriarOpcoes(configuration)).WaitAsync(cancellationToken);
        lock (_trava)
        {
            if (_multiplexador is null) _multiplexador = conectado;
            else conectado.Dispose();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_multiplexador is not null) await _multiplexador.DisposeAsync();
    }

    /// <summary>Porta fora de 1–65535 (ou que não é número) recusa o boot, em vez de cair calada em 6379.</summary>
    public static ConfigurationOptions CriarOpcoes(IConfiguration configuration)
    {
        var porta = 6379;
        var texto = configuration["Redis:Porta"];
        if (!string.IsNullOrWhiteSpace(texto) && (!int.TryParse(texto, out porta) || porta is < 1 or > 65535))
            throw new InvalidOperationException("Redis:Porta precisa ser um número de 1 a 65535.");

        var opcoes = new ConfigurationOptions
        {
            Password = configuration["Redis:Password"],
            AbortOnConnectFail = false,
            BacklogPolicy = BacklogPolicy.FailFast,
            ConnectTimeout = 1000,
            AsyncTimeout = 500,
            SyncTimeout = 500,
            ClientName = "webradio-api",
        };
        opcoes.EndPoints.Add(configuration["Redis:Host"]!, porta);
        return opcoes;
    }
}
