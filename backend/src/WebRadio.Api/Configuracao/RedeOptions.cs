using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace WebRadio.Api.Configuracao;

/// <summary>D17 / S-A06 / S-A11: listeners do Kestrel e rede confiável para o X-Forwarded-For.</summary>
public sealed class RedeOptions
{
    public const string Secao = "Rede";

    public int PortaPublica { get; set; } = 8080;

    public int PortaInterna { get; set; } = 8081;

    /// <summary>IPs em que o listener interno escuta: o IP da rede playout e 127.0.0.1 (o healthcheck roda dentro do contêiner).</summary>
    public string[] BindInterno { get; set; } = [];

    /// <summary>Subnet da rede app, fixa no compose (S-A11). Nunca IP de contêiner: ele muda a cada compose up.</summary>
    public string[] KnownNetworks { get; set; } = [];

    // Os padrões ficam fora dos inicializadores: o binder de configuração ANEXA ao array existente, então um padrão no
    // inicializador permaneceria confiável mesmo quando a configuração define outra rede (alargaria a confiança em silêncio).
    public const string KnownNetworkPadrao = "172.20.0.0/24";

    public const string BindInternoPadrao = "127.0.0.1";

    public string[] RedesConfiaveis => KnownNetworks.Length > 0 ? KnownNetworks : [KnownNetworkPadrao];

    /// <summary>O listener interno nunca pode escutar em todas as interfaces nem na mesma porta do publico (S-A06).</summary>
    public List<string> Validar()
    {
        var falhas = new List<string>();

        if (PortaInterna == PortaPublica)
        {
            falhas.Add("Rede:PortaInterna não pode ser igual à PortaPublica.");
        }

        foreach (var texto in EnderecosInternos)
        {
            if (!IPAddress.TryParse(texto, out var ip))
            {
                falhas.Add($"Rede:BindInterno contém um IP inválido: '{texto}'.");
            }
            else if (IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip))
            {
                falhas.Add("Rede:BindInterno não pode ser 0.0.0.0 nem ::.");
            }
        }

        foreach (var texto in RedesConfiaveis)
        {
            if (!System.Net.IPNetwork.TryParse(texto, out _))
            {
                falhas.Add($"Rede:KnownNetworks contém uma rede inválida: '{texto}'.");
            }
        }

        return falhas;
    }

    public string[] EnderecosInternos => BindInterno.Length > 0 ? BindInterno : [BindInternoPadrao];
}

public static class RedeSetup
{
    /// <summary>Dois listeners: :8080 público (qualquer interface) e :8081 interno, ligado só aos IPs de BindInterno.</summary>
    public static void ConfigurarListeners(this WebApplicationBuilder builder)
    {
        var rede = builder.Configuration.GetSection(RedeOptions.Secao).Get<RedeOptions>() ?? new RedeOptions();

        var falhas = rede.Validar();
        if (falhas.Count > 0)
        {
            throw new InvalidOperationException("Configuração de rede inválida: " + string.Join(" ", falhas));
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(rede.PortaPublica);

            foreach (var ip in rede.EnderecosInternos)
            {
                kestrel.Listen(IPAddress.Parse(ip), rede.PortaInterna);
            }
        });
    }

    /// <summary>
    /// XForwardedFor | XForwardedProto, ForwardLimit = 1 e KnownNetworks = subnet da rede app. Quem não vem dessa rede tem o
    /// cabeçalho ignorado: XFF forjado de fora não cria balde novo no rate limit (S-A11).
    /// </summary>
    public static IServiceCollection AddEncaminhamentoSeguro(this IServiceCollection services, IConfiguration configuration)
    {
        var rede = configuration.GetSection(RedeOptions.Secao).Get<RedeOptions>() ?? new RedeOptions();
        services.Configure<RedeOptions>(configuration.GetSection(RedeOptions.Secao));

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            o.ForwardLimit = 1;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();

            foreach (var rede4 in rede.RedesConfiaveis)
            {
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(rede4));
            }
        });

        return services;
    }
}
