using WebRadio.Application.Comum;
using Xunit;

namespace WebRadio.Application.Tests.Comum;

/// <summary>06 "Metadados vindos do source client" (S-A09), com a exceção decidida no B2: ZWJ e bandeiras com tags.</summary>
public class MetadadoSeguroTests
{
    private const string Escocia = "\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F";

    public static TheoryData<string, string> Aceitos() => new()
    {
        { "texto simples", "Programa da Manhã" },
        { "acento e outros alfabetos", "Canção 日本語 Ελληνικά" },
        { "emoji simples (par de surrogates)", "Ao vivo 🎵" },
        { "família com ZWJ (teste do E1)", "👨‍👩" },
        { "bandeira da Escócia", "Hino " + Escocia },
        { "vazio", "" },
    };

    public static TheoryData<string, string> Recusados() => new()
    {
        { "tag solta", "abc\U000E0067" },
        { "fecho de tag solto", "abc\U000E007F" },
        { "sequência de tags sem U+E007F", "\U0001F3F4\U000E0067\U000E0062" },
        { "tags sem a bandeira na frente", "x\U000E0067\U000E0062\U000E007F" },
        { "zero width space U+200B", "a​b" },
        { "override de direção U+202E", "Faixa‮3pm.exe" },
        { "isolate U+2066", "a⁦b" },
        { "marca de direção U+200F", "a‏b" },
        { "BOM U+FEFF", "﻿titulo" },
        { "hífen suave U+00AD", "a­b" },
        { "quebra de linha", "a\nb" },
        { "NUL", "a\0b" },
        { "C1 U+0085", "a\u0085b" },
        { "separador de linha U+2028", "a\u2028b" },
        { "separador de parágrafo U+2029", "a\u2029b" },
    };

    [Theory]
    [MemberData(nameof(Aceitos))]
    public void Aceita(string caso, string texto) => Assert.True(MetadadoSeguro.Valido(texto), caso);

    [Theory]
    [MemberData(nameof(Recusados))]
    public void Recusa(string caso, string texto) => Assert.False(MetadadoSeguro.Valido(texto), caso);

    // Fora do MemberData: o serializador do xunit troca o surrogate solto por U+FFFD (que é aceito) antes do teste rodar.
    [Fact]
    public void Recusa_surrogate_alto_solto() => Assert.False(MetadadoSeguro.Valido("a\uD83Cb"));

    [Fact]
    public void Recusa_surrogate_baixo_solto() => Assert.False(MetadadoSeguro.Valido("a\uDFB5b"));

    [Fact]
    public void Nulo_e_aceito_o_campo_e_opcional()
    {
        Assert.True(MetadadoSeguro.Valido(null));
    }
}
