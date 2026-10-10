using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Opcoes;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// PBKDF2-SHA256, 100 000 iterações, salt de 16 bytes, hash de 32 bytes. O pepper entra como HMAC-SHA256 da senha antes do
/// PBKDF2: um dump do banco sem o pepper não permite força bruta offline. Formato: v1$iterações$salt$hash (base64).
/// </summary>
public sealed class PasswordHasher : IPasswordHasher
{
    public const int Iteracoes = 100_000;
    public const int TamanhoSalt = 16;
    public const int TamanhoHash = 32;
    private const string Prefixo = "v1";

    private readonly byte[] _pepper;
    private readonly string _hashFicticio;

    public PasswordHasher(IOptions<SegurancaOptions> opcoes)
    {
        _pepper = opcoes.Value.PepperBytes();
        _hashFicticio = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
    }

    public string Hash(string senha)
    {
        var salt = RandomNumberGenerator.GetBytes(TamanhoSalt);
        var hash = Derivar(senha, salt, Iteracoes);
        return $"{Prefixo}${Iteracoes}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verificar(string hashArmazenado, string senha)
    {
        if (!TentarLer(hashArmazenado, out var iteracoes, out var salt, out var esperado))
        {
            // Hash corrompido: gasta o mesmo tempo e recusa.
            Derivar(senha, new byte[TamanhoSalt], Iteracoes);
            return false;
        }

        var calculado = Derivar(senha, salt, iteracoes);
        return CryptographicOperations.FixedTimeEquals(calculado, esperado);
    }

    public void VerificarFicticio(string senha) => Verificar(_hashFicticio, senha);

    private byte[] Derivar(string senha, byte[] salt, int iteracoes)
    {
        var comPepper = HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(senha));
        return Rfc2898DeriveBytes.Pbkdf2(comPepper, salt, iteracoes, HashAlgorithmName.SHA256, TamanhoHash);
    }

    private static bool TentarLer(string armazenado, out int iteracoes, out byte[] salt, out byte[] hash)
    {
        iteracoes = 0;
        salt = [];
        hash = [];

        var partes = armazenado.Split('$');
        if (partes.Length != 4 || partes[0] != Prefixo || !int.TryParse(partes[1], out iteracoes) || iteracoes is < 1 or > 10_000_000)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(partes[2]);
            hash = Convert.FromBase64String(partes[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length == TamanhoSalt && hash.Length == TamanhoHash;
    }
}
