using System.Buffers;
using System.Globalization;
using System.Text;
using FluentValidation;

namespace WebRadio.Application.Comum;

public enum ProblemaDoTexto
{
    Nenhum,

    /// <summary>Surrogate solto (UTF-16 inválido): nem normalizar dá para o texto.</summary>
    MalFormado,

    /// <summary>Controle, formato, uso privado, ponto não atribuído, separador de linha ou ZWJ/tag fora do lugar.</summary>
    CaractereProibido,

    /// <summary>Nada além de espaço e preenchedores invisíveis.</summary>
    SemCaractereVisivel,

    MuitoLongo,
}

/// <summary>
/// 07 E3-D7: o validador dos textos livres do Épico 3 (pedido, motivo da rejeição, título e mensagem da divulgação), que
/// aparecem para o locutor e no Mural público. (1) Normaliza para NFC antes de validar e de persistir, e mede o tamanho
/// DEPOIS, em unidades UTF-16 (o que o <c>nvarchar</c> conta e o <c>string.length</c> do JS mostra). (2) Recusa <c>Cc</c>
/// (inclusive <c>\n</c>, exceto onde o campo aceita), <c>Cf</c> (bidi, ZWSP, BOM), <c>Cs</c>, <c>Co</c> e <c>Cn</c>, e os
/// separadores U+2028/2029. (3) Aceita o ZWJ só entre dois emoji e as tags só dentro de uma bandeira, como no
/// <see cref="MetadadoSeguro"/>. (4) Recusar <c>Cn</c> amarra o validador à versão do Unicode do runtime: um emoji mais novo que as
/// tabelas do .NET da imagem leva 400. (5) Exige ao menos um caractere visível (<c>L</c>, <c>N</c>, <c>P</c> ou <c>S</c>) depois do
/// <c>Trim</c>, fora dos preenchedores. O <see cref="MetadadoSeguro"/> do harbor NÃO muda (o comportamento do ao vivo é outro).
/// </summary>
public static class TextoSeguro
{
    private const int Bandeira = 0x1F3F4;
    private const int TagInicio = 0xE0020;
    private const int TagFim = 0xE007E;
    private const int FechoDeTag = 0xE007F;
    private const int Zwj = 0x200D;
    private const int SeletorDeEmoji = 0xFE0F;

    // Categorias L/N/P/S que não aparecem na tela.
    private static readonly HashSet<int> Preenchedores = [0x3164, 0xFFA0, 0x115F, 0x1160, 0x2800, 0x034F];

    public static ProblemaDoTexto Analisar(string? texto, int maximo, bool aceitaQuebraDeLinha = false)
    {
        if (string.IsNullOrEmpty(texto)) return ProblemaDoTexto.SemCaractereVisivel;
        // Corte barato ANTES de varrer: o campo é anônimo e o corpo pode chegar a centenas de KB. A normalização NFC encolhe o texto no
        // máximo 3:1 (jamo L+V+T → sílaba), então acima de 4× o limite nada do que sobrar cabe, e a regra não muda para o que sobra.
        if (texto.Length > (long)maximo * 4) return ProblemaDoTexto.MuitoLongo;
        if (!BemFormado(texto)) return ProblemaDoTexto.MalFormado;

        var normalizado = texto.Normalize(NormalizationForm.FormC);
        if (!CaracteresPermitidos(normalizado, aceitaQuebraDeLinha)) return ProblemaDoTexto.CaractereProibido;
        if (!TemCaractereVisivel(normalizado)) return ProblemaDoTexto.SemCaractereVisivel;
        // O tamanho vale o do texto que será gravado (NFC e sem as pontas), em unidades UTF-16.
        return Normalizar(texto).Length > maximo ? ProblemaDoTexto.MuitoLongo : ProblemaDoTexto.Nenhum;
    }

    /// <summary>NFC e sem as pontas: a forma que o handler grava e a que o tamanho mede. Texto mal formado volta como veio.</summary>
    public static string Normalizar(string texto) => (BemFormado(texto) ? texto.Normalize(NormalizationForm.FormC) : texto).Trim();

    private static bool BemFormado(string texto)
    {
        for (var i = 0; i < texto.Length;)
        {
            if (Rune.DecodeFromUtf16(texto.AsSpan(i), out _, out var lidos) != OperationStatus.Done) return false;
            i += lidos;
        }

        return true;
    }

    private static bool CaracteresPermitidos(string texto, bool aceitaQuebraDeLinha)
    {
        var runes = texto.EnumerateRunes().ToArray();
        var emBandeira = false; // dentro de U+1F3F4 + tags, esperando o U+E007F
        var anterior = 0;       // último caractere que não é tag
        for (var i = 0; i < runes.Length; i++)
        {
            var v = runes[i].Value;

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

            if (v == Zwj)
            {
                if (!ZwjEntreEmoji(runes, i)) return false;
            }
            else if (!(aceitaQuebraDeLinha && v == '\n') && !Permitido(runes[i]))
            {
                return false;
            }

            anterior = v;
        }

        return !emBandeira;
    }

    /// <summary>O ZWJ junta dois emoji (👨‍👩, ❤️‍🔥): o anterior (pulando o seletor de variação) e o seguinte têm de ser emoji.</summary>
    private static bool ZwjEntreEmoji(Rune[] runes, int posicao)
    {
        var antes = posicao - 1;
        while (antes >= 0 && runes[antes].Value == SeletorDeEmoji) antes--;
        return antes >= 0 && posicao + 1 < runes.Length && EhEmoji(runes[antes].Value) && EhEmoji(runes[posicao + 1].Value);
    }

    private static bool Permitido(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate => false,
        UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned => false,
        UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => false,
        _ => true,
    };

    private static bool TemCaractereVisivel(string texto)
    {
        foreach (var rune in texto.EnumerateRunes())
        {
            if (Preenchedores.Contains(rune.Value)) continue;
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                    or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                    or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber
                    or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
                    or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
                    or UnicodeCategory.OtherPunctuation
                    or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol:
                    return true;
            }
        }

        return false;
    }

    /// <summary>Faixas do Emoji_Presentation / Extended_Pictographic que importam para o ZWJ (sem depender de tabela externa).</summary>
    private static bool EhEmoji(int v) => v is
        0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or 0x24C2 or 0x3030 or 0x303D or 0x3297 or 0x3299
        or (>= 0x2194 and <= 0x21AA) or (>= 0x231A and <= 0x23FF) or (>= 0x25AA and <= 0x25FE) or (>= 0x2600 and <= 0x27BF)
        or (>= 0x2934 and <= 0x2935) or (>= 0x2B05 and <= 0x2B55) or (>= 0x1F000 and <= 0x1FAFF);
}

public static class TextoSeguroExtensions
{
    /// <summary>
    /// Regra FluentValidation do <see cref="TextoSeguro"/>: obrigatório, até <paramref name="maximo"/> unidades UTF-16 depois do NFC. Uma
    /// única chamada a <c>Analisar</c> por campo. A mensagem NUNCA repete o valor recebido (sem <c>{PropertyValue}</c>): o texto pode ter
    /// bidi ou controle. Campo opcional: o chamador envolve a regra em <c>When(x =&gt; x.Campo is { Length: &gt; 0 })</c>.
    /// </summary>
    public static IRuleBuilderOptionsConditions<T, string?> TextoSeguro<T>(this IRuleBuilderInitial<T, string?> regra, int maximo, bool aceitaQuebraDeLinha = false) =>
        regra.Custom((texto, contexto) =>
        {
            var nome = contexto.DisplayName;
            if (string.IsNullOrEmpty(texto))
            {
                contexto.AddFailure($"{nome} é obrigatório.");
                return;
            }

            var mensagem = global::WebRadio.Application.Comum.TextoSeguro.Analisar(texto, maximo, aceitaQuebraDeLinha) switch
            {
                ProblemaDoTexto.MalFormado or ProblemaDoTexto.CaractereProibido => aceitaQuebraDeLinha
                    ? $"{nome} não pode ter caracteres de controle, de formato nem de direção (a quebra de linha é aceita)."
                    : $"{nome} não pode ter caracteres de controle, de formato nem de direção, nem quebra de linha.",
                ProblemaDoTexto.SemCaractereVisivel => $"{nome} precisa ter ao menos um caractere visível.",
                ProblemaDoTexto.MuitoLongo => $"{nome} pode ter no máximo {maximo} caracteres.",
                _ => null,
            };
            if (mensagem is not null) contexto.AddFailure(mensagem);
        });
}
