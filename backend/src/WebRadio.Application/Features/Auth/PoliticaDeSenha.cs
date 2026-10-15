using FluentValidation;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Application.Features.Auth;

/// <summary>
/// Política da senha NOVA (de 12 a 128 caracteres e fora da lista de senhas vazadas, S-B02), num lugar só: Registrar,
/// CriarUsuario, TrocarSenha e o AdminSeeder usam esta mesma regra. O teto de 128 também protege o PBKDF2 de entrada gigante.
/// O login só confere o teto (<see cref="Maximo"/>): não revela nada sobre a política nem sobre contas.
/// </summary>
public static class PoliticaDeSenha
{
    public const int Minimo = 12;
    public const int Maximo = 128;

    public const string MensagemDeTamanho = "A senha precisa ter de 12 a 128 caracteres.";
    public const string MensagemDeVazada = "Esta senha é muito comum. Escolha outra.";

    /// <summary>Null quando a senha serve; senão a mensagem (nunca com o valor da senha).</summary>
    public static string? Verificar(string? senha, ISenhasVazadas vazadas)
    {
        if (string.IsNullOrEmpty(senha) || senha.Length < Minimo || senha.Length > Maximo) return MensagemDeTamanho;
        return vazadas.EhVazada(senha) ? MensagemDeVazada : null;
    }

    /// <summary>Regra de validator para a senha nova: tamanho e lista de vazadas, com as mensagens acima.</summary>
    public static IRuleBuilderOptions<T, string> SenhaNova<T>(this IRuleBuilder<T, string> regra, ISenhasVazadas vazadas) =>
        regra.Must(s => !string.IsNullOrEmpty(s) && s.Length >= Minimo && s.Length <= Maximo).WithMessage(MensagemDeTamanho)
            .Must(s => string.IsNullOrEmpty(s) || !vazadas.EhVazada(s)).WithMessage(MensagemDeVazada);
}
