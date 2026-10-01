using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Seguranca;

namespace WebRadio.Application.Tests.Auth;

public sealed class RelogioFalso(DateTime inicio) : IClock
{
    public DateTime UtcNow { get; set; } = inicio;
}

/// <summary>Conta as verificações PBKDF2: o login precisa gastar EXATAMENTE uma em todo caminho (S-A12).</summary>
public sealed class HasherContador : ISenhaHasher
{
    private readonly SenhaHasher _real = new();
    public int Verificacoes { get; private set; }
    public string Hash(string senha) => _real.Hash(senha);
    public ResultadoSenha Verificar(string hash, string senha) { Verificacoes++; return _real.Verificar(hash, senha); }
    public void VerificarFicticio(string senha) { Verificacoes++; _real.VerificarFicticio(senha); }
}

public sealed class ThrottleFalso : ILoginThrottle
{
    public bool Bloqueado { get; set; }
    public int Falhas { get; private set; }
    public List<string> Chaves { get; } = [];
    public Task<AvaliacaoDeLogin> AvaliarAsync(string e, byte[] i, CancellationToken ct) { Chaves.Add(e); return Task.FromResult(new AvaliacaoDeLogin(Bloqueado, TimeSpan.Zero)); }
    public Task RegistrarFalhaAsync(string e, byte[] i, CancellationToken ct) { Falhas++; return Task.CompletedTask; }
    public Task LimparAsync(string e, byte[] i, CancellationToken ct) => Task.CompletedTask;
}

public static class AuthFixture
{
    public static readonly DateTime Agora = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    public static readonly byte[] IpHash = new byte[32];
    public const string SenhaOk = "uma-senha-bem-longa-123";

    public static RadioDbContext NovoDb(string? nome = null)
        => new(new DbContextOptionsBuilder<RadioDbContext>().UseInMemoryDatabase(nome ?? Guid.NewGuid().ToString()).Options);

    public static Usuario CriarUsuario(RadioDbContext db, ISenhaHasher hasher, string email = "ana@example.com", bool ativo = true,
        Role role = Role.Locutor, bool deveTrocar = false)
    {
        var u = new Usuario(Guid.NewGuid(), "Ana", email, hasher.Hash(SenhaOk), role, deveTrocar, Agora);
        if (!ativo) u.Desativar(ehUltimoAdminAtivo: false, Agora);
        db.Usuarios.Add(u);
        db.SaveChanges();
        return u;
    }

    public static EmissorDeTokens Emissor(IClock? clock = null)
        => new(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48), clock ?? new RelogioFalso(Agora));
}
