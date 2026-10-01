using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// PBKDF2-HMAC-SHA512 do Identity (formato V3) com custo FIXO: não depende do padrão da versão do framework.
/// Hashes gerados com menos iterações verificam como <see cref="ResultadoSenha.OkRehash"/> e são regravados.
/// </summary>
public sealed class SenhaHasher : ISenhaHasher
{
    /// <summary>OWASP (2023): ≥ 210.000 para PBKDF2-HMAC-SHA512. Medido: ~100 ms por verificação num núcleo moderno.</summary>
    public const int Iteracoes = 210_000;

    private readonly PasswordHasher<Usuario> _hasher =
        new(Options.Create(new PasswordHasherOptions { IterationCount = Iteracoes, CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3 }));

    // Hash de uma senha aleatória descartada, com as MESMAS opções: gasta o mesmo PBKDF2 quando não há conta (S-A12).
    private readonly string _hashFicticio;

    public SenhaHasher() => _hashFicticio = _hasher.HashPassword(null!, Guid.NewGuid().ToString("N"));

    public string Hash(string senha) => _hasher.HashPassword(null!, senha);

    public ResultadoSenha Verificar(string hash, string senha)
        => _hasher.VerifyHashedPassword(null!, hash, senha) switch
        {
            PasswordVerificationResult.Success => ResultadoSenha.Ok,
            PasswordVerificationResult.SuccessRehashNeeded => ResultadoSenha.OkRehash,
            _ => ResultadoSenha.Falhou,
        };

    public void VerificarFicticio(string senha) => _ = _hasher.VerifyHashedPassword(null!, _hashFicticio, senha);
}
