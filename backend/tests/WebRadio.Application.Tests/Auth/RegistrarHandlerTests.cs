using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Features.Auth;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Infrastructure.Seguranca;
using Xunit;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Auth;

public class RegistrarHandlerTests
{
    private readonly HasherContador _hasher = new();
    private readonly WebRadio.Infrastructure.Persistencia.RadioDbContext _db = NovoDb();

    [Fact]
    public async Task Registro_cria_Ouvinte_com_hash_e_sem_DeveTrocarSenha()
    {
        var dto = await new RegistrarHandler(_db, _hasher, new RelogioFalso(Agora))
            .Handle(new RegistrarCommand("Bia", "Bia@Example.com", SenhaOk), default);

        Assert.Equal("Ouvinte", dto.Role);
        var u = await _db.Usuarios.SingleAsync();
        Assert.Equal(Role.Ouvinte, u.Role);
        Assert.Equal("BIA@EXAMPLE.COM", u.EmailNormalizado);
        Assert.NotEqual(SenhaOk, u.SenhaHash);
        Assert.Equal(WebRadio.Application.Abstracoes.ResultadoSenha.Ok, _hasher.Verificar(u.SenhaHash, SenhaOk));
    }

    [Fact]
    public async Task Email_repetido_ignora_maiusculas_e_levanta_conflito()
    {
        CriarUsuario(_db, _hasher, "ana@example.com");
        await Assert.ThrowsAsync<EmailJaCadastradoException>(() => new RegistrarHandler(_db, _hasher, new RelogioFalso(Agora))
            .Handle(new RegistrarCommand("Outra", "ANA@example.com", SenhaOk), default));
    }

    [Theory]
    [InlineData("curta")]                       // < 12
    [InlineData("password12345")]               // lista de vazadas
    [InlineData("PASSWORD12345")]               // idem, sem diferenciar caixa
    public void Senha_fraca_e_recusada(string senha)
        => Assert.False(new RegistrarValidator(new SenhasVazadas()).Validate(new RegistrarCommand("Bia", "b@example.com", senha)).IsValid);

    [Fact]
    public void Senha_acima_de_128_ou_email_invalido_ou_nome_vazio_e_recusado()
    {
        var v = new RegistrarValidator(new SenhasVazadas());
        Assert.False(v.Validate(new RegistrarCommand("Bia", "b@example.com", new string('x', 129))).IsValid);
        Assert.False(v.Validate(new RegistrarCommand("Bia", "nao-e-email", SenhaOk)).IsValid);
        Assert.False(v.Validate(new RegistrarCommand("", "b@example.com", SenhaOk)).IsValid);
        Assert.True(v.Validate(new RegistrarCommand("Bia", "b@example.com", SenhaOk)).IsValid);
    }

    /// <summary>
    /// O EF InMemory não impõe índice único; este wrapper simula a corrida: no SaveChanges, OUTRO contexto já
    /// gravou o mesmo e-mail e o banco "reclama" com DbUpdateException.
    /// </summary>
    private sealed class DbComCorrida(WebRadio.Infrastructure.Persistencia.RadioDbContext inner, Func<Task>? outroGravou) : WebRadio.Application.Abstracoes.IRadioDbContext
    {
        public DbSet<WebRadio.Domain.Usuarios.Usuario> Usuarios => inner.Usuarios;
        public DbSet<WebRadio.Domain.Autenticacao.RefreshToken> RefreshTokens => inner.RefreshTokens;
        public DbSet<WebRadio.Domain.Grade.Programa> Programas => inner.Programas;
        public DbSet<WebRadio.Domain.Interacao.PedidoMusica> PedidosMusica => inner.PedidosMusica;
        public DbSet<WebRadio.Domain.Midia.ArquivoMidia> ArquivosMidia => inner.ArquivosMidia;
        public DbSet<WebRadio.Domain.Interacao.Divulgacao> Divulgacoes => inner.Divulgacoes;
        public DbSet<WebRadio.Domain.Midia.Reproducao> Reproducoes => inner.Reproducoes;

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (outroGravou is not null) await outroGravou();
            throw new DbUpdateException("Violation of UNIQUE KEY constraint 'UX_Usuario_EmailNormalizado'.");
        }
    }

    [Fact]
    public async Task Corrida_de_unicidade_no_SaveChanges_vira_o_mesmo_409()
    {
        var nome = Guid.NewGuid().ToString();
        var db = NovoDb(nome);
        var db2 = new DbComCorrida(db, async () =>
        {
            await using var outro = NovoDb(nome);
            CriarUsuario(outro, _hasher, "corrida@example.com");
        });

        await Assert.ThrowsAsync<EmailJaCadastradoException>(() => new RegistrarHandler(db2, _hasher, new RelogioFalso(Agora))
            .Handle(new RegistrarCommand("Dani", "Corrida@Example.com", SenhaOk), default));
    }

    [Fact]
    public async Task Outra_falha_de_banco_no_SaveChanges_nao_vira_409()
    {
        var db2 = new DbComCorrida(NovoDb(), outroGravou: null); // ninguém gravou o e-mail: não é unicidade
        await Assert.ThrowsAsync<DbUpdateException>(() => new RegistrarHandler(db2, _hasher, new RelogioFalso(Agora))
            .Handle(new RegistrarCommand("Eli", "eli@example.com", SenhaOk), default));
    }
}
