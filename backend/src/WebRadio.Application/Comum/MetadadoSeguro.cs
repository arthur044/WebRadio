using System.Globalization;
using System.Text;

namespace WebRadio.Application.Comum;

/// <summary>
/// S-A09 (06 "Metadados vindos do source client"): título e artista seguem para TODOS os clientes do hub e para o log. Recusa as
/// categorias Unicode <c>Cc</c> (controle C0/C1, inclusive quebra de linha e NUL), <c>Cs</c> (surrogate solto) e <c>Cf</c> (formato:
/// U+200B, U+FEFF, U+00AD, as marcas e os controles de direção, como U+202E e U+2066–2069), e os separadores U+2028/2029.
/// Exceção decidida no B2: o ZWJ (U+200D), que forma emoji composto, e as tags U+E0020–U+E007E só dentro de uma bandeira
/// (U+1F3F4, tags, U+E007F). Um validador só para o harbor e para o TocandoAgora, para as regras não se separarem.
/// </summary>
public static class MetadadoSeguro
{
    public const int TamanhoMaximo = 200;

    private const int Bandeira = 0x1F3F4;
    private const int TagInicio = 0xE0020;
    private const int TagFim = 0xE007E;
    private const int FechoDeTag = 0xE007F;
    private const int Zwj = 0x200D;

    /// <summary>
    /// Até <see cref="TamanhoMaximo"/> CARACTERES (code points), como o <c>string.length</c> em UTF-8 do <c>radio.liq</c>. O
    /// <c>MaximumLength</c> do FluentValidation conta unidades UTF-16 e um emoji vale 2: 150 emoji passariam no Liquidsoap e
    /// levariam 400 aqui.
    /// </summary>
    public static bool TamanhoValido(string? texto) => texto is null || texto.EnumerateRunes().Count() <= TamanhoMaximo;

    public static bool Valido(string? texto)
    {
        if (texto is null) return true;

        var anterior = 0;      // último caractere que não é tag
        var emBandeira = false; // dentro de U+1F3F4 + tags, esperando o U+E007F
        for (var i = 0; i < texto.Length;)
        {
            if (Rune.DecodeFromUtf16(texto.AsSpan(i), out var rune, out var lidos) != System.Buffers.OperationStatus.Done)
                return false; // surrogate solto (Cs)
            i += lidos;
            var v = rune.Value;

            if (v is >= TagInicio and <= TagFim)
            {
                if (!(emBandeira || anterior == Bandeira)) return false;
                emBandeira = true;
                continue;
            }

            if (v == FechoDeTag)
            {
                if (!emBandeira) return false;
                emBandeira = false;
                anterior = v;
                continue;
            }

            if (emBandeira) return false; // sequência de tags sem o fecho
            if (!Permitido(rune)) return false;
            anterior = v;
        }

        return !emBandeira;
    }

    private static bool Permitido(Rune rune) => rune.Value == Zwj || Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate => false,
        UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => false,
        _ => true,
    };
}
