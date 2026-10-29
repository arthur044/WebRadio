using FluentValidation;
using WebRadio.Application.Comum;
using Xunit;

namespace WebRadio.Application.Tests.Comum;

/// <summary>07 E3-D7: o validador dos textos livres do Épico 3 (pedido, motivo da rejeição, divulgação).</summary>
public class TextoSeguroTests
{
    private const string Escocia = "\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F";

    public static TheoryData<string, string> Aceitos() => new()
    {
        { "texto simples", "Maria da Silva" },
        { "acento e outros alfabetos", "Canção 日本語 Ελληνικά" },
        { "emoji simples (par de surrogates)", "Toca essa 🎵" },
        { "família com ZWJ entre emoji", "👨\u200D👩\u200D👧" },
        { "ZWJ depois do seletor de variação", "❤\uFE0F\u200D🔥" },
        { "emoji com tom de pele", "👍🏽" },
        { "bandeira por indicadores regionais", "🇧🇷" },
        { "bandeira da Escócia (tags dentro da bandeira)", "Hino " + Escocia },
        { "emoji já atribuído em Unicode 15", "\U0001FAE8" },
        { "pontuação e símbolos", "AC/DC - Back In Black (1980) & cia." },
        { "número", "2025" },
    };

    public static TheoryData<string, string> Recusados() => new()
    {
        { "tabulação", "a\tb" },
        { "quebra de linha", "a\nb" },
        { "retorno de carro", "a\rb" },
        { "NUL", "a\0b" },
        { "C1 U+0085", "a\u0085b" },
        { "zero width space U+200B", "a\u200Bb" },
        { "ZWNJ U+200C", "a\u200Cb" },
        { "override de direção U+202E", "Faixa\u202E3pm.exe" },
        { "isolate U+2066", "a\u2066b" },
        { "marca de direção U+200F", "a\u200Fb" },
        { "BOM U+FEFF", "\uFEFFtitulo" },
        { "hífen suave U+00AD", "a\u00ADb" },
        { "separador de linha U+2028", "a\u2028b" },
        { "separador de parágrafo U+2029", "a\u2029b" },
        { "uso privado U+E000", "a\uE000b" },
        { "ponto de código não atribuído U+0378", "a\u0378b" },
        { "ZWJ entre letras", "a\u200Db" },
        { "ZWJ no começo", "\u200D🎵" },
        { "ZWJ no fim", "🎵\u200D" },
        { "tag solta", "abc\U000E0067" },
        { "fecho de tag solto", "abc\U000E007F" },
        { "sequência de tags sem U+E007F", "\U0001F3F4\U000E0067\U000E0062" },
        { "tags sem a bandeira na frente", "x\U000E0067\U000E0062\U000E007F" },
    };

    public static TheoryData<string, string> SemCaractereVisivel() => new()
    {
        { "só espaços", "     " },
        { "só espaço ideográfico", "\u3000" },
        { "Hangul filler U+3164", "\u3164" },
        { "halfwidth Hangul filler U+FFA0", "\uFFA0" },
        { "Hangul choseong filler U+115F", "\u115F" },
        { "Hangul jungseong filler U+1160", "\u1160" },
        { "braille em branco U+2800", "\u2800" },
        { "combining grapheme joiner U+034F", "\u034F" },
        { "espaços e preenchedores", " \u3164 \u2800 " },
    };

    [Theory]
    [MemberData(nameof(Aceitos))]
    public void Aceita(string caso, string texto) => Assert.True(ProblemaDoTexto.Nenhum == TextoSeguro.Analisar(texto, 60), caso);

    [Theory]
    [MemberData(nameof(Recusados))]
    public void Recusa_caractere_proibido(string caso, string texto) =>
        Assert.True(ProblemaDoTexto.CaractereProibido == TextoSeguro.Analisar(texto, 60), caso);

    [Theory]
    [MemberData(nameof(SemCaractereVisivel))]
    public void Recusa_texto_sem_caractere_visivel(string caso, string texto) =>
        Assert.True(ProblemaDoTexto.SemCaractereVisivel == TextoSeguro.Analisar(texto, 60), caso);

    // Fora do MemberData: o serializador do xunit troca o surrogate solto por U+FFFD (que é aceito) antes do teste rodar.
    [Fact]
    public void Recusa_surrogate_alto_solto() => Assert.Equal(ProblemaDoTexto.MalFormado, TextoSeguro.Analisar("a\uD83Cb", 60));

    [Fact]
    public void Recusa_surrogate_baixo_solto() => Assert.Equal(ProblemaDoTexto.MalFormado, TextoSeguro.Analisar("a\uDFB5b", 60));

    [Fact]
    public void Nulo_e_vazio_nao_tem_caractere_visivel()
    {
        Assert.Equal(ProblemaDoTexto.SemCaractereVisivel, TextoSeguro.Analisar(null, 60));
        Assert.Equal(ProblemaDoTexto.SemCaractereVisivel, TextoSeguro.Analisar("", 60));
    }

    [Fact]
    public void A_quebra_de_linha_so_vale_quando_o_campo_aceita()
    {
        Assert.Equal(ProblemaDoTexto.CaractereProibido, TextoSeguro.Analisar("linha 1\nlinha 2", 100));
        Assert.Equal(ProblemaDoTexto.Nenhum, TextoSeguro.Analisar("linha 1\nlinha 2", 100, aceitaQuebraDeLinha: true));
        Assert.Equal(ProblemaDoTexto.SemCaractereVisivel, TextoSeguro.Analisar("\n\n", 100, aceitaQuebraDeLinha: true));
        // O retorno de carro continua recusado: o texto guarda só \n.
        Assert.Equal(ProblemaDoTexto.CaractereProibido, TextoSeguro.Analisar("a\r\nb", 100, aceitaQuebraDeLinha: true));
    }

    [Fact]
    public void O_tamanho_conta_unidades_UTF16_depois_da_normalizacao()
    {
        // 30 emoji = 60 unidades UTF-16: cabe em 60. 31 não cabe, e 60 emoji (120 unidades) também não.
        Assert.Equal(ProblemaDoTexto.Nenhum, TextoSeguro.Analisar(string.Concat(Enumerable.Repeat("😀", 30)), 60));
        Assert.Equal(ProblemaDoTexto.MuitoLongo, TextoSeguro.Analisar(string.Concat(Enumerable.Repeat("😀", 31)), 60));
        Assert.Equal(ProblemaDoTexto.MuitoLongo, TextoSeguro.Analisar(string.Concat(Enumerable.Repeat("😀", 60)), 60));
        Assert.Equal(ProblemaDoTexto.Nenhum, TextoSeguro.Analisar(new string('a', 60), 60));
        Assert.Equal(ProblemaDoTexto.MuitoLongo, TextoSeguro.Analisar(new string('a', 61), 60));
    }

    [Fact]
    public void Normaliza_para_NFC_antes_de_medir()
    {
        var decomposto = string.Concat(Enumerable.Repeat("e\u0301", 60)); // e + U+0301: 120 unidades; em NFC são 60
        Assert.Equal(120, decomposto.Length);
        Assert.Equal(ProblemaDoTexto.Nenhum, TextoSeguro.Analisar(decomposto, 60));
        Assert.Equal(60, TextoSeguro.Normalizar(decomposto).Length);
        Assert.Equal("\u00E9", TextoSeguro.Normalizar("e\u0301"));
    }

    [Fact]
    public void Normalizar_apara_as_pontas()
    {
        Assert.Equal("Maria", TextoSeguro.Normalizar("  Maria\u00A0 "));
    }

    [Fact]
    public void Normalizar_nao_lanca_com_surrogate_solto()
    {
        // O validador recusa o texto antes; o handler nunca chega aqui com ele, mas a função não pode explodir.
        Assert.Equal("a\uD83Cb", TextoSeguro.Normalizar("a\uD83Cb"));
    }

    private sealed record Corpo(string? Nome, string? Mensagem);

    private sealed class CorpoValidator : AbstractValidator<Corpo>
    {
        public CorpoValidator()
        {
            RuleFor(x => x.Nome).TextoSeguro(10);
            When(x => x.Mensagem is { Length: > 0 }, () => RuleFor(x => x.Mensagem).TextoSeguro(20, aceitaQuebraDeLinha: true));
        }
    }

    [Fact]
    public void A_mensagem_de_erro_nao_repete_o_valor_recebido()
    {
        const string segredo = "SEGREDO\u202EABC";
        var resultado = new CorpoValidator().Validate(new Corpo(segredo, null));

        var erro = Assert.Single(resultado.Errors);
        Assert.Equal("Nome", erro.PropertyName);
        Assert.DoesNotContain("SEGREDO", erro.ErrorMessage);
        Assert.DoesNotContain("ABC", erro.ErrorMessage);
    }

    [Fact]
    public void O_validador_responde_um_erro_por_campo_e_aceita_o_texto_valido()
    {
        var validador = new CorpoValidator();

        Assert.True(validador.Validate(new Corpo("Maria", null)).IsValid);
        Assert.True(validador.Validate(new Corpo("Maria", "")).IsValid);
        Assert.True(validador.Validate(new Corpo("Maria", "oi\ntchau")).IsValid);
        Assert.Contains(validador.Validate(new Corpo(null, null)).Errors, e => e.PropertyName == "Nome");
        Assert.Contains(validador.Validate(new Corpo("Maria", "   ")).Errors, e => e.PropertyName == "Mensagem");
        Assert.Contains(validador.Validate(new Corpo(new string('x', 11), null)).Errors, e => e.PropertyName == "Nome");
        Assert.Single(validador.Validate(new Corpo("a\0b", null)).Errors);
    }
}
