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
    public void A_familia_nasce_no_proprio_token_e_o_teto_de_30_dias_limita_a_expiracao_do_sucessor()
    {
        var primeiro = Criar();
        Assert.Equal(Agora, primeiro.FamiliaCriadaEmUtc);
        Assert.Equal(TimeSpan.FromDays(30), RefreshToken.TetoDaFamilia);

        // dia 27: 7 dias dariam o dia 34, mas a família morre no dia 30
        var noDia27 = new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), Hash32, Guid.NewGuid(), Agora.AddDays(27), TimeSpan.FromDays(7), null, familiaCriadaEmUtc: Agora);
        Assert.Equal(Agora.AddDays(30), noDia27.ExpiraEmUtc);
        Assert.Equal(Agora, noDia27.FamiliaCriadaEmUtc);

        // longe do teto, a duração normal vale
        var noDia10 = new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), Hash32, Guid.NewGuid(), Agora.AddDays(10), TimeSpan.FromDays(7), null, familiaCriadaEmUtc: Agora);
        Assert.Equal(Agora.AddDays(17), noDia10.ExpiraEmUtc);
    }

    [Fact]
    public void Familia_no_teto_ou_alem_dele_nao_gera_token_e_a_data_da_familia_nao_pode_ser_futura()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), Hash32, Guid.NewGuid(), Agora.AddDays(30), TimeSpan.FromDays(7), null, familiaCriadaEmUtc: Agora));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), Hash32, Guid.NewGuid(), Agora.AddDays(40), TimeSpan.FromDays(7), null, familiaCriadaEmUtc: Agora));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), Hash32, Guid.NewGuid(), Agora, TimeSpan.FromDays(7), null, familiaCriadaEmUtc: Agora.AddDays(1)));
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
        Assert.Equal(Agora.AddHours(1), token.RevogadoEmUtc);
        Assert.False(token.EstaValido(Agora.AddHours(3)));
    }

    [Fact]
    public void MarcarSubstituidoPor_rotaciona_e_revoga()
    {
        var token = Criar();
        var proximoId = Guid.NewGuid();

        token.MarcarSubstituidoPor(proximoId, Agora.AddDays(1));

        Assert.True(token.EstaRevogado);
        Assert.Equal(proximoId, token.SubstituidoPorId);
    }

    [Fact]
    public void Construtor_copia_o_TokenHash_M6()
    {
        var hash = new byte[32];
        var token = new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), hash, Guid.NewGuid(), Agora, TimeSpan.FromDays(7), null);

        hash[0] = 0xFF;

        Assert.NotEqual((byte)0xFF, token.TokenHash[0]);
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
