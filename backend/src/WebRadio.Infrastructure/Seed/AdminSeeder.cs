using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Seed;

/// <summary>
/// E1-F07: cria o primeiro Admin a partir de Seed__AdminEmail/Seed__AdminSenha, só se não existir nenhum Admin. Nasce com
/// DeveTrocarSenha = true. Roda sob sp_getapplock 'seg:admins' (D22) para duas APIs subindo juntas não criarem dois.
/// </summary>
public sealed class AdminSeeder(RadioDbContext db, ISenhaHasher hasher, IClock clock, IConfiguration config, ILogger<AdminSeeder> logger)
    : IInicializadorDeDados
{
    public async Task ExecutarAsync(CancellationToken ct)
    {
        var email = config["Seed:AdminEmail"];
        var senha = config["Seed:AdminSenha"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(senha)) return;

        var estrategia = db.Database.CreateExecutionStrategy();
        var resultado = await estrategia.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await SemaforoAdmins.AdquirirAsync(db, ct);

            if (await db.Usuarios.AnyAsync(u => u.Role == Role.Admin, ct)) return ResultadoSeed.JaExiste;

            var normalizado = email.Trim().ToUpperInvariant();
            // /auth/registrar é público: o e-mail pode ter sido cadastrado como Ouvinte antes do primeiro boot.
            // Não promovemos conta de terceiros nem derrubamos o boot; o operador escolhe outro e-mail.
            if (await db.Usuarios.AnyAsync(u => u.EmailNormalizado == normalizado, ct)) return ResultadoSeed.EmailEmUso;

            db.Usuarios.Add(new Usuario(Guid.CreateVersion7(), "Administrador", email.Trim(), hasher.Hash(senha), Role.Admin,
                deveTrocarSenha: true, clock.UtcNow));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ResultadoSeed.Criado;
        });

        // Sem e-mail nem senha no log; só o fato (S-B07: a senha de seed deve sair do ambiente depois do primeiro boot).
        if (resultado == ResultadoSeed.Criado)
            logger.LogWarning("ADMIN_SEMEADO: Admin inicial criado com DeveTrocarSenha. Remova Seed__AdminSenha do ambiente");
        else if (resultado == ResultadoSeed.EmailEmUso)
            logger.LogError("ADMIN_SEED_CONFLITO: Seed__AdminEmail já pertence a uma conta que não é Admin; nenhum Admin foi criado");
    }

    private enum ResultadoSeed { JaExiste, EmailEmUso, Criado }
}

/// <summary>Exclusão mútua entre APIs sobre os Admins (D22): applock transacional, liberado no commit/rollback.</summary>
internal static class SemaforoAdmins
{
    public static Task AdquirirAsync(RadioDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'seg:admins', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000; IF @r < 0 THROW 50001, N'applock seg:admins indisponível', 1;",
            ct);
}
