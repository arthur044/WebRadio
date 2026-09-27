using WebRadio.Domain.Autenticacao;
using WebRadio.Domain.Erros;
using Xunit;

namespace WebRadio.Domain.Tests.Autenticacao;

public class RefreshTokenTests
{
    private static readonly DateTime Agora = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] Hash32 = new byte[32];

    private static RefreshToken Criar(DateTime? criadoEmUtc = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Hash32, Guid.NewGuid(), criadoEmUtc ?? Agora, TimeSpan.FromDays(7), null);

    [Fact]
    public void Construtor_calcula_expiracao_a_partir_da_duracao()
    {
        var token = Criar();

        Assert.Equal(Agora.AddDays(7), token.ExpiraEmUtc);
        Assert.False(token.EstaRevogado);
    }

    [Fact]
    public void Construtor_rejeita_hash_de_tamanho_errado()
    {
        Assert.Throws<ArgumentException>(() =>
            new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), new byte[16], Guid.NewGuid(), Agora, TimeSpan.FromDays(7), null));
    }

    [Fact]
    public void EstaValido_true_antes_de_expirar_e_sem_revogacao()
    {
        var token = Criar();

        Assert.True(token.EstaValido(Agora.AddDays(1)));
    }

    [Fact]
    public void EstaValido_false_apos_expirar()
    {
        var token = Criar();

        Assert.False(token.EstaValido(Agora.AddDays(8)));
    }

    [Fact]
    public void Revogar_e_idempotente()
    {
        var token = Criar();

        token.Revogar(Agora.AddHours(1));
        token.Revogar(Agora.AddHours(2)); // não deve lançar, nem sobrescrever o timestamp original

        Assert.True(token.EstaRevogado);
        Assert.False(token.EstaValido(Agora.AddHours(3)));
    }

    [Fact]
    public void MarcarSubstituidoPor_rotaciona_e_revoga()
    {
        var token = Criar();
        var proximoId = Guid.NewGuid();

        token.MarcarSubstituidoPor(proximoId, Agora.AddDays(1));

        Assert.True(token.EstaRevogado);
    }

    [Fact]
    public void MarcarSubstituidoPor_em_token_ja_revogado_lanca_TransicaoInvalida()
    {
        var token = Criar();
        token.Revogar(Agora.AddHours(1));

        Assert.Throws<TransicaoInvalidaException>(() =>
            token.MarcarSubstituidoPor(Guid.NewGuid(), Agora.AddHours(2)));
    }
}
