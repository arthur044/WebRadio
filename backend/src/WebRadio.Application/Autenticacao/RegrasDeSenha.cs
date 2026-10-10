using FluentValidation;

namespace WebRadio.Application.Autenticacao;

/// <summary>02-api-rest §2: senha de 12 a 128 caracteres, checada contra uma lista local de senhas vazadas (S-B02).</summary>
public static class RegrasDeSenha
{
    public const int Minimo = 12;
    public const int Maximo = 128;

    // Amostra das senhas de 12+ caracteres mais comuns em vazamentos. A lista completa pode substituir esta sem mudar o contrato.
    private static readonly HashSet<string> Vazadas = new(StringComparer.OrdinalIgnoreCase)
    {
        "123456789012", "1234567890123", "12345678901234", "123456789abc", "qwertyuiop12", "qwertyuiopas",
        "qwertyuiop123", "password1234", "password12345", "passw0rd1234", "senha1234567", "senha12345678",
        "mudar123456", "mudar@123456", "administrador", "administrator", "welcome12345", "letmein12345",
        "iloveyou1234", "abcdefghijkl", "abc123456789", "000000000000", "111111111111", "aaaaaaaaaaaa",
        "radiowebradio", "webradio1234", "webradio12345",
    };

    public static IRuleBuilderOptions<T, string> SenhaValida<T>(this IRuleBuilder<T, string> regra) =>
        regra
            .NotEmpty().WithMessage("Informe a senha.")
            .MinimumLength(Minimo).WithMessage($"A senha precisa ter de {Minimo} a {Maximo} caracteres.")
            .MaximumLength(Maximo).WithMessage($"A senha precisa ter de {Minimo} a {Maximo} caracteres.")
            .Must(s => !Vazadas.Contains(s)).WithMessage("Essa senha é muito comum. Escolha outra.");

    public static bool EhVazada(string senha) => Vazadas.Contains(senha);
}
