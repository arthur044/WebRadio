using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebRadio.Application.Autenticacao;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Opcoes;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Seguranca;

namespace WebRadio.Infrastructure.Tests.Suporte;

/// <summary>Monta a pilha real (hasher, JWT, refresh, handlers) sobre um banco SQL Server de verdade e relógio/Redis falsos.</summary>
public sealed class Cenario(BancoDeTeste banco)
{
    public const string Senha = "senha-de-teste-123";

    public RelogioFalso Relogio { get; } = new(DateTime.UtcNow);

    public ThrottleEmMemoria Throttle { get; } = new();

    public IOptions<SegurancaOptions> Seguranca { get; } = Segredos.Seguranca();

    public IOptions<JwtOptions> Jwt { get; } = Segredos.Jwt();

    public PasswordHasher Hasher => new(Seguranca);

    public JwtService Tokens => new(Jwt, Relogio);

    public ListenerIdentityService Identidade => new(Seguranca);

    public RadioDbContext NovoDb() => banco.CriarContexto();

    public RefreshTokenService NovoRefresh(RadioDbContext? db = null) =>
        new(db ?? NovoDb(), Relogio, Jwt, Seguranca, NullLogger<RefreshTokenService>.Instance);

    public LoginBloqueio Bloqueio => new(Throttle, NullLogger<LoginBloqueio>.Instance);

    public LoginHandler NovoLogin(RadioDbContext? db = null) =>
        new(db ?? NovoDb(), Hasher, Tokens, NovoRefresh(), Identidade, Bloqueio);

    public RefreshHandler NovoRefreshHandler(RadioDbContext? db = null)
    {
        db ??= NovoDb();
        return new RefreshHandler(db, Tokens, NovoRefresh(db), Identidade);
    }

    public async Task<Usuario> CriarUsuarioAsync(string email, Role role = Role.Locutor, bool deveTrocarSenha = false, string senha = Senha)
    {
        await using var db = NovoDb();
        var usuario = new Usuario(Guid.CreateVersion7(), "Fulano", email, Hasher.Hash(senha), role, deveTrocarSenha, Relogio.UtcAgora);
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();
        return usuario;
    }
}
