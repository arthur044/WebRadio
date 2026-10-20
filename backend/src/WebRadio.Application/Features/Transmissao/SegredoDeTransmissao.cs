using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WebRadio.Application.Features.Transmissao;

/// <summary>Segredo gerado: o texto aparece uma única vez (resposta do POST); o banco guarda só salt e hash.</summary>
public sealed record SegredoGerado(string Segredo, byte[] Salt, byte[] Hash)
{
    public override string ToString() => nameof(SegredoGerado);
}

/// <summary>
/// Contrato da credencial de transmissão (06, S-A08). O que entra no hash é o texto de 43 caracteres que o locutor digita, não
/// os 32 bytes de origem: a mesma função na geração e na verificação. Com 256 bits de entropia, SHA-256 com salt basta.
/// </summary>
public static partial class SegredoDeTransmissao
{
    public const int TamanhoSegredo = 43;
    private const int BytesDoSegredo = 32;

    // \z e não $: em .NET o $ casa também antes de um \n final, e "guid\n" passaria.
    [GeneratedRegex(@"\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RegexUsuario();

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RegexSenha();

    private static readonly byte[] SaltFicticio = RandomNumberGenerator.GetBytes(CredencialTamanhos.Salt);
    private static readonly byte[] HashFicticio = RandomNumberGenerator.GetBytes(CredencialTamanhos.Hash);

    public static bool UsuarioValido(string? usuario) => usuario is not null && RegexUsuario().IsMatch(usuario);

    public static bool SenhaValida(string? senha) => senha is not null && RegexSenha().IsMatch(senha);

    /// <summary>UUID v4 aleatório: nunca v7 nem sequencial, que carregam o relógio.</summary>
    public static Guid NovoIdentificador() => Guid.NewGuid();

    public static SegredoGerado Gerar()
    {
        var segredo = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(BytesDoSegredo));
        var salt = RandomNumberGenerator.GetBytes(CredencialTamanhos.Salt);
        return new SegredoGerado(segredo, salt, Hash(salt, segredo));
    }

    public static byte[] Hash(ReadOnlySpan<byte> salt, string segredo)
    {
        var texto = Encoding.ASCII.GetBytes(segredo);
        var dados = new byte[salt.Length + texto.Length];
        salt.CopyTo(dados);
        texto.CopyTo(dados.AsSpan(salt.Length));
        return SHA256.HashData(dados);
    }

    public static bool Confere(byte[] salt, byte[] hash, string segredo) =>
        CryptographicOperations.FixedTimeEquals(Hash(salt, segredo), hash);

    /// <summary>Identificador válido que não existe: o mesmo trabalho, para o tempo não revelar se ele existe.</summary>
    public static void ConferirFicticio(string segredo) => _ = Confere(SaltFicticio, HashFicticio, segredo);

    private static class CredencialTamanhos
    {
        public const int Salt = Domain.Transmissao.CredencialTransmissao.TamanhoSaltBytes;
        public const int Hash = Domain.Transmissao.CredencialTransmissao.TamanhoHashBytes;
    }
}
