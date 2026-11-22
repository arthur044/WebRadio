using System.Security.Cryptography;
using System.Text;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// Subchaves por uso derivadas do <c>Seguranca:Pepper</c> com HKDF-SHA256 (RFC 5869; #17). O pepper nunca entra direto em um HMAC: cada uso (IP, pseudônimo de log, chave opaca de
/// armazenamento, dispositivo do ouvinte e sucessor do refresh) tem a sua própria subchave, então um valor produzido para um uso não serve de oráculo para outro. Sem segredo novo: a
/// derivação é determinística a partir do pepper (<c>info</c> = <c>webradio/pepper/v1/&lt;uso&gt;</c>, sal zero, 32 bytes). Trocar o pepper ou a <c>v1</c> troca TODAS as subchaves.
/// </summary>
public static class SubchavesDoPepper
{
    public const string Ip = "ip";
    public const string Pseudonimo = "pseudonimo";
    public const string Chave = "chave";
    public const string Dispositivo = "device";
    public const string Refresh = "refresh";

    private const int TamanhoEmBytes = 32;

    public static byte[] Derivar(byte[] pepper, string uso)
    {
        ArgumentNullException.ThrowIfNull(pepper);
        ArgumentException.ThrowIfNullOrWhiteSpace(uso);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, pepper, TamanhoEmBytes, salt: null, info: Encoding.UTF8.GetBytes("webradio/pepper/v1/" + uso));
    }
}
