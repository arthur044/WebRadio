using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Opcoes;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Seed;

/// <summary>
/// E1-F07: cria o primeiro Admin a partir de Seed__AdminEmail/Seed__AdminSenha, só se não existir nenhum Admin. Nasce com
/// DeveTrocarSenha = true. Roda sob sp_getapplock 'seg:admins' (D22) para duas APIs subindo juntas não criarem dois.
/// </summary>
public sealed class AdminSeeder(
    RadioDbContext db,
    IPasswordHasher hasher,
    IClock clock,
    IOptions<SeedOptions> opcoes,
    ILogger<AdminSeeder> logger) : IInicializadorDeDados
{
    public async Task ExecutarAsync(CancellationToken cancellationToken = default)
    {
        var o = opcoes.Value;
        if (string.IsNullOrWhiteSpace(o.AdminEmail) || string.IsNullOrWhiteSpace(o.AdminSenha))
        {
            return;
        }

        var estrategia = db.Database.CreateExecutionStrategy();
        var criado = await estrategia.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            await SemaforoAdmins.AdquirirAsync(db, cancellationToken);

            if (await db.Usuarios.AnyAsync(u => u.Role == Role.Admin, cancellationToken))
            {
                return false;
            }

            var normalizado = o.AdminEmail.Trim().ToUpperInvariant();
            if (await db.Usuarios.AnyAsync(u => u.EmailNormalizado == normalizado, cancellationToken))
            {
                // /auth/registrar e publico: o e-mail pode ter sido cadastrado como Ouvinte antes do primeiro boot.
                // Nao promovemos conta de terceiros nem derrubamos o boot; o operador escolhe outro e-mail.
                logger.LogError("ADMIN_SEED_CONFLITO: Seed__AdminEmail ja pertence a uma conta que nao e Admin; nenhum Admin foi criado");
                return false;
            }

            var admin = new Usuario(
                Guid.CreateVersion7(), "Administrador", o.AdminEmail.Trim(), hasher.Hash(o.AdminSenha), Role.Admin,
                deveTrocarSenha: true, clock.UtcAgora);

            db.Usuarios.Add(admin);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return true;
        });

        // S-B07: sem e-mail nem senha no log; só o fato. A senha de seed deve sair do ambiente depois do primeiro boot.
        if (criado)
        {
            logger.LogWarning("ADMIN_SEMEADO: Admin inicial criado com DeveTrocarSenha. Remova Seed__AdminSenha do ambiente");
        }
    }
}

/// <summary>Exclusão mútua de duas APIs sobre os Admins (D22): applock transacional, liberado no commit/rollback.</summary>
internal static class SemaforoAdmins
{
    public static Task AdquirirAsync(RadioDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'seg:admins', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000; IF @r < 0 THROW 50001, N'applock seg:admins indisponível', 1;",
            ct);
}
