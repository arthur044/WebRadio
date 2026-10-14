using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Auth;
using WebRadio.Application.Features.Usuarios;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using WebRadio.Infrastructure.Seguranca;

namespace WebRadio.Infrastructure.Tests.Suporte;

public sealed class RelogioFalso(DateTime inicio) : IClock
{
    public DateTime UtcNow { get; private set; } = DateTime.SpecifyKind(inicio, DateTimeKind.Utc);

    public void Avancar(TimeSpan tempo) => UtcNow = UtcNow.Add(tempo);
}

/// <summary>Monta a pilha real (hasher, tokens, refresh, handlers) sobre um SQL Server de verdade e relógio falso.</summary>
public sealed class Cenario(BancoDeTeste banco)
{
    public const string Senha = "senha-de-teste-123";
    public static readonly byte[] IpA = Enumerable.Repeat((byte)0xA, 32).ToArray();
    public static readonly byte[] IpB = Enumerable.Repeat((byte)0xB, 32).ToArray();

    public RelogioFalso Relogio { get; } = new(DateTime.UtcNow);

    public byte[] Pepper { get; } = RandomNumberGenerator.GetBytes(48);

    public byte[] ChaveJwt { get; } = RandomNumberGenerator.GetBytes(48);

    public SenhaHasher Hasher { get; } = new();

    public EmissorDeTokens Tokens => new(ChaveJwt, Relogio);

    public MemoryLoginThrottle Throttle { get; private set; } = null!;

    public ISenhasVazadas Vazadas { get; } = new SenhasVazadas();

    public RadioDbContext NovoDb() => banco.CriarContexto();

    public RefreshTokenService NovoRefresh(RadioDbContext? db = null, byte[]? pepper = null) =>
        new(db ?? NovoDb(), Relogio, Tokens, pepper ?? Pepper, NullLogger<RefreshTokenService>.Instance);

    public LoginHandler NovoLogin() =>
        new(NovoDb(), Hasher, Tokens, ThrottleReal(), Relogio);

    public RefreshHandler NovoRefreshHandler()
    {
        var db = NovoDb();
        return new RefreshHandler(db, NovoRefresh(db), Tokens);
    }

    public TrocarSenhaHandler NovoTrocarSenha()
    {
        var db = NovoDb();
        return new TrocarSenhaHandler(db, Hasher, NovoRefresh(db), ThrottleReal(), NovaVersaoDeSeguranca(db), new OutboxEf(db), Relogio);
    }

    /// <summary>Um cache só para o cenário inteiro, como o singleton da Api: é ele que segura a versão por 15 s.</summary>
    public MemoryCache Cache { get; } = new(new MemoryCacheOptions());

    public VersaoDeSeguranca NovaVersaoDeSeguranca(RadioDbContext? db = null) => new(db ?? NovoDb(), Cache);

    public AlterarUsuarioHandler NovoAlterarUsuario()
    {
        var db = NovoDb();
        return new AlterarUsuarioHandler(db, new TransacaoComTrava(db), NovoRefresh(db), new OutboxEf(db), NovaVersaoDeSeguranca(db), Relogio);
    }

    public MemoryLoginThrottle ThrottleReal() =>
        Throttle ??= new MemoryLoginThrottle(Relogio, new IpHasher(Pepper), NullLogger<MemoryLoginThrottle>.Instance);

    public async Task<Usuario> CriarUsuarioAsync(string email, Role role = Role.Locutor, bool deveTrocarSenha = false)
    {
        await using var db = NovoDb();
        var usuario = new Usuario(Guid.CreateVersion7(), "Fulano", email, Hasher.Hash(Senha), role, deveTrocarSenha, Relogio.UtcNow);
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();
        return usuario;
    }

    public static string Email() => $"user-{Guid.NewGuid():N}@example.com";

    public static string TokenAleatorio() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}
