using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
        var nome = Guid.NewGuid().ToString();
        var db = NovoDb(nome);
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

        // Persistido de verdade (SaveChanges): um contexto NOVO, sem o rastreamento do anterior, enxerga o hash novo.
        await using var outro = NovoDb(nome);
        var salvo = await outro.Usuarios.AsNoTracking().SingleAsync();
        Assert.NotEqual(antigo, salvo.SenhaHash);
        Assert.Equal(ResultadoSenha.Ok, new SenhaHasher().Verificar(salvo.SenhaHash, SenhaOk));
        Assert.True(salvo.DeveTrocarSenha);
    }
}

public class SenhaHasherSemaforoTests
{
    [Fact]
    public async Task Verificacoes_simultaneas_nunca_passam_do_limite_de_concorrencia()
    {
        var h = new SenhaHasher(limiteConcorrencia: 2, esperaMaxima: TimeSpan.FromSeconds(60));
        var hash = h.Hash("Senha-forte-123");
        var tarefas = Enumerable.Range(0, 8).Select(_ => Task.Run(() => h.Verificar(hash, "Senha-forte-123"))).ToArray();
        await Task.WhenAll(tarefas);
        Assert.All(tarefas, t => Assert.Equal(ResultadoSenha.Ok, t.Result));
        Assert.True(h.PicoDeConcorrencia <= 2, $"pico {h.PicoDeConcorrencia}");
    }

    [Fact]
    public async Task Sem_vaga_dentro_da_espera_lanca_ServidorOcupado()
    {
        var h = new SenhaHasher(limiteConcorrencia: 1, esperaMaxima: TimeSpan.FromMilliseconds(1));
        var hash = h.Hash("Senha-forte-123");
        using var largada = new Barrier(6);
        var tarefas = Enumerable.Range(0, 6).Select(_ => Task.Factory.StartNew(() =>
        {
            largada.SignalAndWait();
            try { h.Verificar(hash, "Senha-forte-123"); return false; }
            catch (WebRadio.Domain.Erros.ServidorOcupadoException) { return true; }
        }, TaskCreationOptions.LongRunning)).ToArray();
        var rejeitadas = (await Task.WhenAll(tarefas)).Count(x => x);
        Assert.True(rejeitadas > 0);
        Assert.True(h.PicoDeConcorrencia <= 1);
    }

    [Fact]
    public void Limite_invalido_e_recusado()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SenhaHasher(limiteConcorrencia: 0, esperaMaxima: TimeSpan.FromSeconds(1)));
}
