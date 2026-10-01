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
        Assert.True(_hasher.Verificar(u.SenhaHash, SenhaOk));
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
}
