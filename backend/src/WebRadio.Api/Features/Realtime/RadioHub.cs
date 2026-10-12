using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Seguranca;

namespace WebRadio.Api.Features.Realtime;

/// <summary>
/// 02 §10. Sem métodos cliente → servidor: toda ação passa pela REST. Os grupos são decididos AQUI, a partir do
/// <c>listenerId</c> da query e do JWT; o cliente não escolhe grupo.
/// </summary>
public sealed class RadioHub(IListenerIdentity identidade, ConexoesDoHub conexoes) : Hub
{
    public const string Rota = "/hubs/radio";

    public override async Task OnConnectedAsync()
    {
        // S-M09: listenerId precisa ser um UUID de verdade; senão a conexão é recusada.
        var bruto = Context.GetHttpContext()?.Request.Query["listenerId"].ToString();
        if (!Guid.TryParseExact(bruto, "D", out var listenerId) || listenerId == Guid.Empty)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, identidade.GrupoListener(listenerId));

        var usuario = Context.User;
        if (usuario?.Identity?.IsAuthenticated == true && Guid.TryParse(usuario.FindFirst("sub")?.Value, out var usuarioId))
        {
            conexoes.Registrar(usuarioId, Context);
            // PedidoCriado leva nome e dedicatória dos ouvintes: só para quem modera, e nunca com o token restrito.
            if ((usuario.IsInRole("Locutor") || usuario.IsInRole("Admin")) && !usuario.HasClaim(EmissorDeTokens.ClaimDeveTrocarSenha, "true"))
                await Groups.AddToGroupAsync(Context.ConnectionId, GruposDoHub.Locutores);
        }

        await base.OnConnectedAsync();
        conexoes.MarcarPronta(Context.ConnectionId);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        conexoes.Remover(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Conexões autenticadas DESTA instância, por usuário: o SignalR não tem "derrubar as conexões do usuário X". Com mais de
/// uma réplica, a ordem de derrubar chega a todas pelo Redis (ver <see cref="SignalRRealtimeNotifier"/>).
/// </summary>
public sealed class ConexoesDoHub
{
    private readonly ConcurrentDictionary<string, (Guid UsuarioId, HubCallerContext Contexto)> _porConexao = new();
    private readonly ConcurrentDictionary<string, byte> _prontas = new();

    /// <summary>Conexões autenticadas rastreadas (as únicas que podem ser derrubadas por usuário).</summary>
    public int Total => _porConexao.Count;

    /// <summary>
    /// Conexões (anônimas inclusive) que já terminaram o <c>OnConnectedAsync</c>, ou seja, já estão nos grupos. O handshake
    /// responde ao cliente ANTES disso: é o número que diz quantas já recebem eventos (diagnóstico e testes).
    /// </summary>
    public int Prontas => _prontas.Count;

    public void MarcarPronta(string connectionId) => _prontas[connectionId] = 0;

    public void Registrar(Guid usuarioId, HubCallerContext contexto) => _porConexao[contexto.ConnectionId] = (usuarioId, contexto);

    public void Remover(string connectionId)
    {
        _porConexao.TryRemove(connectionId, out _);
        _prontas.TryRemove(connectionId, out _);
    }

    /// <summary>Aborta as conexões locais do usuário e devolve quantas eram. O cliente reconecta e passa pela checagem da <c>sv</c>.</summary>
    public int Derrubar(Guid usuarioId)
    {
        var derrubadas = 0;
        foreach (var (connectionId, conexao) in _porConexao)
        {
            if (conexao.UsuarioId != usuarioId) continue;
            conexao.Contexto.Abort();
            _porConexao.TryRemove(connectionId, out _);
            derrubadas++;
        }

        return derrubadas;
    }
}
