using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace WebRadio.Api.Configuracao;

/// <summary>
/// 05 §5.3: mascara propriedades chamadas *Password*, *Senha*, *Token*, *Key*, *Pepper* (e Secret, Authorization, Cookie, Email)
/// em qualquer nível de estrutura. Nunca logar e-mail, senha, token ou IP em claro.
/// </summary>
public sealed partial class MascaraSegredosEnricher : ILogEventEnricher
{
    public const string Mascara = "***";

    [GeneratedRegex("password|senha|token|key|pepper|secret|authorization|cookie|email", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NomeSensivel();

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var propriedade in logEvent.Properties.ToArray())
        {
            var mascarado = Mascarar(propriedade.Key, propriedade.Value);
            if (!ReferenceEquals(mascarado, propriedade.Value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(propriedade.Key, mascarado));
            }
        }
    }

    internal static LogEventPropertyValue Mascarar(string nome, LogEventPropertyValue valor)
    {
        if (NomeSensivel().IsMatch(nome))
        {
            return new ScalarValue(Mascara);
        }

        return valor switch
        {
            StructureValue s => MascararEstrutura(s),
            DictionaryValue d => new DictionaryValue(d.Elements.Select(e => KeyValuePair.Create(
                e.Key,
                e.Key.Value is string chave ? Mascarar(chave, e.Value) : Mascarar(string.Empty, e.Value)))),
            SequenceValue q => new SequenceValue(q.Elements.Select(e => Mascarar(string.Empty, e))),
            _ => valor,
        };
    }

    private static StructureValue MascararEstrutura(StructureValue s) =>
        new(s.Properties.Select(p => new LogEventProperty(p.Name, Mascarar(p.Name, p.Value))), s.TypeTag);
}

public static class LogSetup
{
    /// <summary>
    /// Serilog em JSON. O nível de Microsoft.AspNetCore fica em Warning porque o log de início de requisição do Hosting
    /// inclui a query string (access_token do hub, S-M09); quem registra a requisição é o UseSerilogRequestLogging, só com o path.
    /// </summary>
    public static void ConfigurarSerilog(this WebApplicationBuilder builder) =>
        builder.Host.UseSerilog((_, configuracao) => configuracao
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.With<MascaraSegredosEnricher>()
            .WriteTo.Console(new RenderedCompactJsonFormatter()));
}
