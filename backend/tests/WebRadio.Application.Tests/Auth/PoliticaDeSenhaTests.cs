using FluentValidation;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Auth;
using Xunit;

namespace WebRadio.Application.Tests.Auth;

/// <summary>Os limites 12/128 vivem num lugar só (#19): Registrar, CriarUsuario, TrocarSenha e o AdminSeeder usam a mesma regra.</summary>
public class PoliticaDeSenhaTests
{
    private sealed class Vazadas(params string[] lista) : ISenhasVazadas
    {
        public bool EhVazada(string senha) => lista.Contains(senha, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Os_limites_sao_12_e_128()
    {
        Assert.Equal(12, PoliticaDeSenha.Minimo);
        Assert.Equal(128, PoliticaDeSenha.Maximo);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("curta-demais", false)] // 12 caracteres: passa no tamanho
    [InlineData("onze-chars", true)]    // 10
    public void Verificar_recusa_vazia_e_curta(string senha, bool recusada)
        => Assert.Equal(recusada, PoliticaDeSenha.Verificar(senha, new Vazadas()) is not null);

    [Fact]
    public void Verificar_aceita_128_e_recusa_129()
    {
        var v = new Vazadas();
        Assert.Null(PoliticaDeSenha.Verificar(new string('a', 128), v));
        Assert.NotNull(PoliticaDeSenha.Verificar(new string('a', 129), v));
    }

    [Fact]
    public void Verificar_recusa_senha_vazada_sem_repetir_o_valor()
    {
        var erro = PoliticaDeSenha.Verificar("senha-vazada-123", new Vazadas("senha-vazada-123"));

        Assert.NotNull(erro);
        Assert.DoesNotContain("senha-vazada-123", erro);
    }

    [Fact]
    public void Verificar_trata_nulo_como_vazio()
        => Assert.NotNull(PoliticaDeSenha.Verificar(null, new Vazadas()));

    [Theory]
    [InlineData("curta", false)]
    [InlineData("uma-senha-boa-e-longa", true)]
    public void A_regra_de_validator_usa_a_mesma_politica(string senha, bool valida)
    {
        var validator = new InlineValidator<string>();
        validator.RuleFor(x => x).SenhaNova(new Vazadas());

        Assert.Equal(valida, validator.Validate(senha).IsValid);
    }
}
