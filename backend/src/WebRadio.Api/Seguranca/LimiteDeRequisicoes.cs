using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using WebRadio.Api.Configuracao;
using WebRadio.Api.Erros;

namespace WebRadio.Api.Seguranca;

public static class LimiteDeRequisicoes
{
    public const string Login = "login";
    public const string Registrar = "registrar";
    public const string TrocarSenha = "trocar-senha";
    public const string Sessao = "sessao";
    private const int GlobalPorMinuto = 100;

    /// <summary>
    /// Global de 100/min por IP; /auth/login 5/min por IP; /auth/registrar 3/h por IP (02-api-rest §2). Depende do
    /// ForwardedHeaders correto (S-A11): sem ele, a audiência inteira cairia no mesmo balde. O listener interno não é limitado.
    /// </summary>
    public static IServiceCollection AddLimitesWebRadio(this IServiceCollection services)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(contexto =>
            {
                var rede = contexto.RequestServices.GetRequiredService<IOptions<RedeOptions>>().Value;
                if (contexto.Connection.LocalPort == rede.PortaInterna)
                {
                    return RateLimitPartition.GetNoLimiter("interno");
                }

                return RateLimitPartition.GetFixedWindowLimiter(Ip(contexto), _ => Janela(GlobalPorMinuto, TimeSpan.FromMinutes(1)));
            });

            o.AddPolicy(Login, c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => Janela(5, TimeSpan.FromMinutes(1))));
            o.AddPolicy(TrocarSenha, c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => Janela(10, TimeSpan.FromMinutes(1))));
            o.AddPolicy(Sessao, c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => Janela(30, TimeSpan.FromMinutes(1))));
            o.AddPolicy(Registrar, c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => Janela(3, TimeSpan.FromHours(1))));

            o.OnRejected = async (contexto, ct) =>
            {
                var resposta = contexto.HttpContext.Response;
                resposta.StatusCode = StatusCodes.Status429TooManyRequests;

                var problema = ProblemasHttp.Criar(StatusCodes.Status429TooManyRequests, ProblemasHttp.Tipos.LimiteDeRequisicoes, "Muitas requisições");
                if (contexto.Lease.TryGetMetadata(MetadataName.RetryAfter, out var espera))
                {
                    var segundos = (int)Math.Ceiling(espera.TotalSeconds);
                    resposta.Headers.RetryAfter = segundos.ToString(CultureInfo.InvariantCulture);
                    problema.Extensions["retryAfterSeconds"] = segundos;
                }

                await resposta.WriteAsJsonAsync(problema, options: null, contentType: "application/problem+json", cancellationToken: ct);
            };
        });

        return services;
    }

    // Chave de partição por IP: o RemoteIpAddress já vem corrigido pelo ForwardedHeaders (só da KnownNetworks).
    private static string Ip(HttpContext contexto)
    {
        var ip = contexto.Connection.RemoteIpAddress ?? IPAddress.None;
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return ip.ToString();
        }

        // Um /64 e um unico assinante: sem isto, um endereco novo por requisicao zera todos os limites.
        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private static FixedWindowRateLimiterOptions Janela(int limite, TimeSpan janela) => new()
    {
        PermitLimit = limite,
        Window = janela,
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}
