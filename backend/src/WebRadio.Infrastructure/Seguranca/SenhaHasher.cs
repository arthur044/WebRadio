using Microsoft.AspNetCore.Identity;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Infrastructure.Seguranca;

public sealed class SenhaHasher : ISenhaHasher
{
    private readonly PasswordHasher<Usuario> _hasher = new();

    // Hash de uma senha aleatória descartada: gasta o mesmo PBKDF2 quando não há conta (S-A12).
    private readonly string _hashFicticio;

    public SenhaHasher() => _hashFicticio = _hasher.HashPassword(null!, Guid.NewGuid().ToString("N"));

    public string Hash(string senha) => _hasher.HashPassword(null!, senha);

    public bool Verificar(string hash, string senha)
        => _hasher.VerifyHashedPassword(null!, hash, senha) != PasswordVerificationResult.Failed;

    public void VerificarFicticio(string senha) => _ = _hasher.VerifyHashedPassword(null!, _hashFicticio, senha);
}
