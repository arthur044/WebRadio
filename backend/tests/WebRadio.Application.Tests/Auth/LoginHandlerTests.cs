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
        Assert.NotEqual(r.Refresh.Token, Convert.ToBase64String(guardado.TokenHash)); // o valor opaco não vai para o banco
    }

    [Fact]
    public async Task Login_acima_do_teto_de_sessoes_revoga_as_familias_mais_antigas_e_so_elas()
    {
        var u = CriarUsuario(_db, _hasher);
        // 10 sessões (famílias) ativas, da mais antiga à mais nova, e uma sessão de OUTRO usuário (nunca é tocada).
        var familias = Enumerable.Range(0, LoginHandler.MaximoDeSessoesPorUsuario).Select(_ => Guid.NewGuid()).ToArray();
        for (var i = 0; i < familias.Length; i++)
            _db.RefreshTokens.Add(new WebRadio.Domain.Autenticacao.RefreshToken(Guid.NewGuid(), u.Id, Enumerable.Repeat((byte)i, 32).ToArray(),
                familias[i], Agora.AddMinutes(-100 + i), TimeSpan.FromDays(7), null));
        var outro = CriarUsuario(_db, _hasher, email: "outro@example.com");
        _db.RefreshTokens.Add(new WebRadio.Domain.Autenticacao.RefreshToken(Guid.NewGuid(), outro.Id, Enumerable.Repeat((byte)99, 32).ToArray(),
            Guid.NewGuid(), Agora.AddMinutes(-200), TimeSpan.FromDays(7), null));
        await _db.SaveChangesAsync();

        await Handler().Handle(new LoginCommand("ana@example.com", SenhaOk, IpHash), default);

        var ativas = await _db.RefreshTokens.Where(t => t.UsuarioId == u.Id && t.RevogadoEmUtc == null).Select(t => t.FamiliaId).ToListAsync();
        Assert.Equal(LoginHandler.MaximoDeSessoesPorUsuario, ativas.Count);          // a nova entrou; o teto vale
        Assert.DoesNotContain(familias[0], ativas);                                    // a mais antiga saiu
        Assert.All(familias.Skip(1), f => Assert.Contains(f, ativas));                 // as outras ficaram
        Assert.Single(await _db.RefreshTokens.Where(t => t.UsuarioId == outro.Id && t.RevogadoEmUtc == null).ToListAsync()); // a de outro usuário não
    }

    [Fact]
    public async Task Login_abaixo_do_teto_nao_revoga_nada()
    {
        var u = CriarUsuario(_db, _hasher);
        for (var i = 0; i < 3; i++)
            await Handler().Handle(new LoginCommand("ana@example.com", SenhaOk, IpHash), default);

        Assert.Equal(3, await _db.RefreshTokens.CountAsync(t => t.UsuarioId == u.Id && t.RevogadoEmUtc == null));
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
