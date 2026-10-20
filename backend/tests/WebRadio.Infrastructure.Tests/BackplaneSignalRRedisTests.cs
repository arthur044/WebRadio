using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// O pin central força o StackExchange.Redis 3 também no backplane do SignalR (o pacote da Microsoft foi compilado contra o 2.x).
/// Aqui dois hosts Kestrel dividem o mesmo Redis: o que a Api manda num host tem de chegar ao ouvinte conectado no outro.
/// </summary>
public class BackplaneSignalRRedisTests
{
    public sealed class HubDeTeste : Hub;

    [RedisFact]
    public async Task Mensagem_enviada_por_um_host_chega_ao_cliente_conectado_no_outro_pelo_backplane_Redis()
    {
        var destino = Environment.GetEnvironmentVariable("WEBRADIO_TEST_REDIS")!;
        var prefixo = "webradio-teste-" + Guid.NewGuid().ToString("N")[..8];

        await using var hostA = await SubirAsync(destino, prefixo);
        await using var hostB = await SubirAsync(destino, prefixo);

        var recebido = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cliente = new HubConnectionBuilder().WithUrl(hostB.Urls.First() + "/hub").Build();
        cliente.On<string>("msg", texto => recebido.TrySetResult(texto));
        await cliente.StartAsync();

        // O cliente só entra na assinatura do canal no Redis depois de conectar: tenta algumas vezes até chegar.
        var contexto = hostA.Services.GetRequiredService<IHubContext<HubDeTeste>>();
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!recebido.Task.IsCompleted)
        {
            await contexto.Clients.All.SendAsync("msg", "via-backplane", prazo.Token);
            await Task.WhenAny(recebido.Task, Task.Delay(250, prazo.Token));
        }

        Assert.Equal("via-backplane", await recebido.Task);
    }

    private static async Task<WebApplication> SubirAsync(string redis, string prefixo)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR().AddStackExchangeRedis(redis, o => o.Configuration.ChannelPrefix = RedisChannel.Literal(prefixo));

        var app = builder.Build();
        app.MapHub<HubDeTeste>("/hub");
        await app.StartAsync();
        return app;
    }
}
