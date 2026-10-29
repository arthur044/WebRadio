using Microsoft.Extensions.Options;

namespace WebRadio.Application.Features.Pedidos;

/// <summary>
/// Seção <c>Pedidos</c> (07, E3-F01, E3-D10, E3-F04). <c>LimitePorIp</c> sobe para CGNAT e Wi-Fi de escola; <c>TetoPendentes</c> é o teto
/// global de <c>Pendente</c> (S-M13); <c>ExpiracaoMinutos</c> é a idade a partir da qual o Worker expira o pedido pendente.
/// </summary>
public sealed class PedidosOptions
{
    public const string Secao = "Pedidos";

    public const int LimitePorIpPadrao = 3;
    public const int TetoPendentesPadrao = 300;
    public const int ExpiracaoMinutosPadrao = 120;

    public int LimitePorIp { get; set; } = LimitePorIpPadrao;

    public int TetoPendentes { get; set; } = TetoPendentesPadrao;

    public int ExpiracaoMinutos { get; set; } = ExpiracaoMinutosPadrao;
}

/// <summary>Valor fora da faixa falha o boot em vez de virar um throttle que bloqueia tudo (0) ou nada.</summary>
public sealed class PedidosOptionsValidator : IValidateOptions<PedidosOptions>
{
    public ValidateOptionsResult Validate(string? name, PedidosOptions o)
    {
        var erros = new List<string>();
        if (o.LimitePorIp is < 1 or > 1000) erros.Add("Pedidos:LimitePorIp deve estar entre 1 e 1000.");
        if (o.TetoPendentes is < 1 or > 100_000) erros.Add("Pedidos:TetoPendentes deve estar entre 1 e 100000.");
        if (o.ExpiracaoMinutos is < 5 or > 10_080) erros.Add("Pedidos:ExpiracaoMinutos deve estar entre 5 e 10080 (7 dias).");
        return erros.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(erros);
    }
}
