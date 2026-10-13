namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// S-C02: o boot recusa segredo vazio, de exemplo ou curto. Compartilhada pela Api (<c>RegraSegredo</c>) e pelo Worker, que
/// não passa pelo <c>ValidateOnStart</c> da Api. A mensagem nunca traz o valor.
/// </summary>
public static class ValidacaoDeSegredo
{
    public static readonly string[] Marcadores = ["__GERAR__", "CHANGE_ME", "CHANGEME"];

    /// <summary>Mínimo de caracteres das senhas geradas (05 §5.1: <c>openssl rand -base64 24</c> dá 32).</summary>
    public const int MinimoDeSenha = 24;

    /// <summary>Vazio ou valor de exemplo; <c>null</c> = ok.</summary>
    public static string? VazioOuExemplo(string chave, string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
            return $"{chave} está vazio. Rode infra/scripts/gerar-segredos.sh.";
        if (Marcadores.Any(m => valor.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return $"{chave} contém um valor de exemplo (__GERAR__/CHANGE_ME). Gere um segredo real.";
        return null;
    }

    /// <summary>Vazio, de exemplo ou com menos de <paramref name="minimo"/> caracteres; <c>null</c> = ok.</summary>
    public static string? Verificar(string chave, string? valor, int minimo = MinimoDeSenha) =>
        VazioOuExemplo(chave, valor)
        ?? (valor!.Length < minimo ? $"{chave} tem menos de {minimo} caracteres." : null);
}
