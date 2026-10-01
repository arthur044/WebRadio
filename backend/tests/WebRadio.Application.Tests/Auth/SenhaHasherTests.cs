using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Auth;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Seguranca;
using Xunit;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Auth;

public class SenhaHasherTests
{
    private static string HashAntigo(string senha, int iteracoes)
        => new PasswordHasher<Usuario>(Options.Create(new PasswordHasherOptions { IterationCount = iteracoes })).HashPassword(null!, senha);

    [Fact]
    public void Custo_e_fixo_em_210_mil_e_hash_novo_verifica_como_Ok()
    {
        Assert.True(SenhaHasher.Iteracoes >= 210_000);
        var h = new SenhaHasher();
        Assert.Equal(ResultadoSenha.Ok, h.Verificar(h.Hash(SenhaOk), SenhaOk));
        Assert.Equal(ResultadoSenha.Falhou, h.Verificar(h.Hash(SenhaOk), "outra-senha-qualquer"));
    }

    [Fact]
    public void Hash_com_menos_iteracoes_verifica_como_OkRehash()
        => Assert.Equal(ResultadoSenha.OkRehash, new SenhaHasher().Verificar(HashAntigo(SenhaOk, 10_000), SenhaOk));

    [Fact]
    public async Task Login_com_hash_antigo_regrava_o_hash_com_o_custo_atual_sem_mexer_em_DeveTrocarSenha_nem_emitir_evento()
    {
        var db = NovoDb();
        var hasher = new HasherContador();
        var u = new Usuario(Guid.NewGuid(), "Ana", "ana@example.com", HashAntigo(SenhaOk, 10_000), WebRadio.Domain.Enums.Role.Admin, true, Agora);
        db.Usuarios.Add(u);
        db.SaveChanges();
        var antigo = u.SenhaHash;

        await new LoginHandler(db, hasher, Emissor(), new ThrottleFalso(), new RelogioFalso(Agora))
            .Handle(new LoginCommand("ana@example.com", SenhaOk, IpHash), default);

        Assert.NotEqual(antigo, u.SenhaHash);
        Assert.Equal(ResultadoSenha.Ok, new SenhaHasher().Verificar(u.SenhaHash, SenhaOk));
        Assert.True(u.DeveTrocarSenha);
        Assert.Empty(u.DomainEvents);
    }
}
