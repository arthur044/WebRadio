using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using WebRadio.Api.Infra;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Persistencia.Outbox;

namespace WebRadio.Api.Features.Realtime;

/// <summary>Só o OutboxProcessor chama isto (D22): handlers gravam no outbox, nunca falam com o hub.</summary>
public sealed class SignalRRealtimeNotifier(IHubContext<RadioHub> hub, ConexoesDoHub conexoes, IServiceProvider servicos) : IRealtimeNotifier
{
    /// <summary>Canal próprio (fora do prefixo do backplane): leva só o id do usuário cujas conexões devem cair.</summary>
    public static readonly RedisChannel CanalDeDesconexao = RedisChannel.Literal("webradio:hub:desconectar");

    public Task NotificarTodosAsync(string evento, object? carga, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync(evento, carga, ct);

    public Task NotificarGruposAsync(IReadOnlyList<string> grupos, string evento, object? carga, CancellationToken ct = default) =>
        hub.Clients.Groups(grupos).SendAsync(evento, carga, ct);

    public async Task DesconectarUsuarioAsync(Guid usuarioId, CancellationToken ct = default)
    {
        conexoes.Derrubar(usuarioId);

        // Com Redis (várias réplicas possíveis), as outras instâncias ficam sabendo pelo canal. Se a publicação falhar, a
        // exceção sobe e o outbox tenta de novo: a ordem de derrubar não se perde.
        if (servicos.GetService<IConnectionMultiplexer>() is { } redis)
            await redis.GetSubscriber().PublishAsync(CanalDeDesconexao, usuarioId.ToString("N")).WaitAsync(ct);
    }
}

/// <summary>Em cada réplica: ouve o canal de desconexão e derruba as conexões locais do usuário.</summary>
public sealed class AssinanteDeDesconexoes(IConnectionMultiplexer redis, ConexoesDoHub conexoes, ILogger<AssinanteDeDesconexoes> logger) : BackgroundService
{
    private static readonly TimeSpan EsperaEntreTentativas = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // O Redis pode estar fora no boot (AbortOnConnectFail = false): insiste até assinar. Depois disso o próprio
        // multiplexer refaz a assinatura a cada reconexão.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await redis.GetSubscriber().SubscribeAsync(SignalRRealtimeNotifier.CanalDeDesconexao, (_, valor) =>
                {
                    if (Guid.TryParseExact((string?)valor, "N", out var usuarioId)) conexoes.Derrubar(usuarioId);
                });
                return;
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                logger.LogWarning(ex, "REDIS_INDISPONIVEL: assinatura do canal de desconexão do hub falhou; nova tentativa em {Segundos}s", EsperaEntreTentativas.TotalSeconds);
            }

            try
            {
                await Task.Delay(EsperaEntreTentativas, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

public sealed class RealtimeEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app) =>
        // Ouvinte anônimo também conecta (02 §10); o JWT, quando vem, só decide o grupo `locutores`.
        // S-M08: a conexão cai quando o access token expira; o cliente reconecta com o token renovado.
        app.MapHub<RadioHub>(RadioHub.Rota, o => o.CloseOnAuthenticationExpiration = true).AllowAnonymous();

    public static IServiceCollection AddRealtime(IServiceCollection services, IConfiguration configuration)
    {
        var signalr = services
            .AddSignalR(o =>
            {
                // Não há método cliente → servidor: 1 KB basta para o handshake e os pings (S-M08).
                o.MaximumReceiveMessageSize = 1024;
                o.EnableDetailedErrors = false;
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddSingleton<ConexoesDoHub>();
        services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();
        services.AddScoped<OutboxProcessor>();
        // Desligável só para os testes controlarem o passe à mão (Outbox:Dispatcher=false).
        services.AddScoped<WebRadio.Application.Features.Harbor.ReconciliadorDoAoVivo>();
        if (!bool.TryParse(configuration["Outbox:Dispatcher"], out var ligado) || ligado)
        {
            services.AddHostedService<OutboxDispatcher>();
            services.AddHostedService<WebRadio.Infrastructure.Harbor.ReconciliadorDoAoVivoJob>();
            services.AddHostedService<WebRadio.Infrastructure.Harbor.AlertaDoHarborJob>();
        }

        if (string.IsNullOrWhiteSpace(configuration["Redis:Host"])) return services;

        // Backplane: com 2+ réplicas, o evento publicado por uma chega aos clientes das outras. Usa o MESMO multiplexer
        // (senha e timeouts já validados) em vez de abrir uma segunda configuração de Redis.
        signalr.AddStackExchangeRedis(o => o.Configuration.ChannelPrefix = RedisChannel.Literal("webradio"));
        services.AddOptions<Microsoft.AspNetCore.SignalR.StackExchangeRedis.RedisOptions>()
            .Configure<IConnectionMultiplexer>((o, redis) => o.ConnectionFactory = _ => Task.FromResult(redis));
        services.AddHostedService<AssinanteDeDesconexoes>();
        return services;
    }
}
