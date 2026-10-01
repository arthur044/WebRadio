using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Features.Auth;
using WebRadio.Domain.Erros;
using Xunit;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Auth;

public class LoginHandlerTests
{
    private readonly HasherContador _hasher = new();
    private readonly ThrottleFalso _throttle = new();
    private readonly WebRadio.Infrastructure.Persistencia.RadioDbContext _db = NovoDb();

    private LoginHandler Handler() => new(_db, _hasher, Emissor(), _throttle, new RelogioFalso(Agora));

    [Fact]
    public async Task Login_valido_devolve_token_e_persiste_so_o_hash_do_refresh()
    {
        var u = CriarUsuario(_db, _hasher);
        var r = await Handler().Handle(new LoginCommand(" ANA@Example.com ", SenhaOk, IpHash), default);

        Assert.Equal(u.Id, r.Usuario.Id);
        Assert.False(r.DeveTrocarSenha);
        var guardado = await _db.RefreshTokens.SingleAsync();
        Assert.Equal(u.Id, guardado.UsuarioId);
        Assert.Equal(32, guardado.TokenHash.Length);
        Assert.Equal(r.Refresh.Hash, guardado.TokenHash);
        Assert.NotEqual(r.Refresh.Valor, Convert.ToBase64String(guardado.TokenHash)); // o valor opaco não vai para o banco
    }

    [Fact]
    public async Task Senha_errada_email_inexistente_inativo_e_bloqueado_dao_a_MESMA_excecao_e_UMA_verificacao_de_senha()
    {
        CriarUsuario(_db, _hasher);
        CriarUsuario(_db, _hasher, "off@example.com", ativo: false);

        async Task<(Exception ex, int verificacoes)> Tentar(string email, string senha, bool bloqueado = false)
        {
            _throttle.Bloqueado = bloqueado;
            var antes = _hasher.Verificacoes;
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => Handler().Handle(new LoginCommand(email, senha, IpHash), default));
            return (ex, _hasher.Verificacoes - antes);
        }

        var casos = new[]
        {
            await Tentar("ana@example.com", "errada-errada-errada"),
            await Tentar("naoexiste@example.com", SenhaOk),
            await Tentar("off@example.com", SenhaOk),
            await Tentar("ana@example.com", SenhaOk, bloqueado: true), // senha CERTA, mas bloqueado
        };

        Assert.All(casos, c => Assert.IsType<CredenciaisInvalidasException>(c.ex));
        Assert.All(casos, c => Assert.Equal(1, c.verificacoes));
        Assert.Equal(casos[0].ex.Message, casos[1].ex.Message);
    }

    [Fact]
    public async Task Falha_conta_no_throttle_mas_bloqueado_nao_acumula()
    {
        CriarUsuario(_db, _hasher);
        await Assert.ThrowsAsync<CredenciaisInvalidasException>(() => Handler().Handle(new LoginCommand("ana@example.com", "errada-errada-1", IpHash), default));
        Assert.Equal(1, _throttle.Falhas);
        _throttle.Bloqueado = true;
        await Assert.ThrowsAsync<CredenciaisInvalidasException>(() => Handler().Handle(new LoginCommand("ana@example.com", "errada-errada-2", IpHash), default));
        Assert.Equal(1, _throttle.Falhas);
    }

    [Fact]
    public async Task Login_devolve_DeveTrocarSenha_do_usuario_semeado()
    {
        CriarUsuario(_db, _hasher, "admin@example.com", role: WebRadio.Domain.Enums.Role.Admin, deveTrocar: true);
        var r = await Handler().Handle(new LoginCommand("admin@example.com", SenhaOk, IpHash), default);
        Assert.True(r.DeveTrocarSenha);
    }

    [Fact]
    public void ToString_do_comando_nao_vaza_email_nem_senha()
    {
        var texto = new LoginCommand("ana@example.com", SenhaOk, IpHash).ToString();
        Assert.DoesNotContain("ana@", texto);
        Assert.DoesNotContain(SenhaOk, texto);
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("a@b.c", "")]
    public void Validador_recusa_vazios_e_tamanho_absurdo(string email, string senha)
        => Assert.False(new LoginValidator().Validate(new LoginCommand(email, senha, IpHash)).IsValid);

    [Fact]
    public void Validador_recusa_senha_acima_de_128()
        => Assert.False(new LoginValidator().Validate(new LoginCommand("a@b.c", new string('x', 129), IpHash)).IsValid);
}
