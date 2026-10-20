using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace WebRadio.Application.Features.Harbor;

/// <summary>
/// <c>Harbor__AtrasDoProxy</c> (06 "Throttle do harbor/auth", último item). Atrás do <c>harbor-borda</c> (E2-F11) o Liquidsoap só vê
/// o IP do proxy: o par (identificador, IP) viraria bloqueio por identificador, que é proibido (lockout do locutor). Com true a
/// API não bloqueia por IP nem por par, só registra e alerta; o limite por cliente fica no nginx. Padrão false (acesso direto).
/// <c>Harbor__HostPublico</c> e <c>Harbor__PortaPublica</c> (06 "Credencial de transmissão", <c>conexao</c>): o que o locutor digita
/// no source client. Só o <c>docker-compose.live.yml</c> os define, junto da porta do harbor-borda.
/// </summary>
public sealed class HarborOptions
{
    public const string Secao = "Harbor";

    /// <summary>O mount do harbor é fixo no <c>radio.liq</c> (<c>input.harbor("live")</c>).</summary>
    public const string Mount = "/live";

    public bool AtrasDoProxy { get; set; }

    /// <summary>Nome ou IP pelo qual o locutor alcança o harbor. Vazio = não configurado (<c>conexao.host</c> vai <c>null</c>).</summary>
    public string? HostPublico { get; set; }

    public int PortaPublica { get; set; } = 8005;

    public ConexaoDoHarbor Conexao() => new(string.IsNullOrWhiteSpace(HostPublico) ? null : HostPublico.Trim(), PortaPublica, Mount);
}

/// <summary>Como o locutor aponta o BUTT/Mixxx para o ao vivo. Não é segredo: vai junto da credencial, em <c>GET</c> e <c>POST</c>.</summary>
public sealed record ConexaoDoHarbor(string? Host, int Porta, string Mount);

/// <summary>Falha o boot com host que não é nome/IP ou porta fora de 1–65535: o valor errado chegaria à tela do locutor.</summary>
public sealed partial class HarborOptionsValidator : IValidateOptions<HarborOptions>
{
    [GeneratedRegex(@"^[A-Za-z0-9.:\[\]-]{1,253}\z")]
    private static partial Regex HostValido();

    public ValidateOptionsResult Validate(string? name, HarborOptions o)
    {
        var erros = new List<string>();
        if (o.PortaPublica is < 1 or > 65535) erros.Add("Harbor:PortaPublica deve estar entre 1 e 65535.");
        if (!string.IsNullOrWhiteSpace(o.HostPublico) && !HostValido().IsMatch(o.HostPublico.Trim()))
            erros.Add("Harbor:HostPublico deve ser um nome de host ou IP (letras, dígitos, '.', '-', ':' e colchetes).");
        return erros.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(erros);
    }
}
