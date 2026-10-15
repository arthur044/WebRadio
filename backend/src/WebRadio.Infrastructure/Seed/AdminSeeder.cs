using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Auth;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Seed;

/// <summary>
/// E1-F07: cria o primeiro Admin a partir de Seed__AdminEmail/Seed__AdminSenha, só se não existir nenhum Admin. Nasce com
/// DeveTrocarSenha = true. Roda sob sp_getapplock 'seg:admins' (D22) para duas APIs subindo juntas não criarem dois.
/// </summary>
public sealed class AdminSeeder(RadioDbContext db, ISenhaHasher hasher, IClock clock, IConfiguration config, ISenhasVazadas vazadas,
    ILogger<AdminSeeder> logger)
    : IInicializadorDeDados
{
    public async Task ExecutarAsync(CancellationToken ct)
    {
        var email = config["Seed:AdminEmail"];
        var senha = config["Seed:AdminSenha"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(senha)) return;

        // S-B07: a senha de seed só serve para o primeiro boot. Enquanto ela estiver no ambiente (no compose, o arquivo de
        // segredo continua montado), todo boot lembra de removê-la; o aviso vale mesmo depois de o Admin existir.
        logger.LogWarning("ADMIN_SEED_NO_AMBIENTE: Seed__AdminSenha continua no ambiente; remova depois do primeiro boot (o Admin troca a senha no primeiro login)");

        var estrategia = db.Database.CreateExecutionStrategy();
        var resultado = await estrategia.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await TransacaoComTrava.AdquirirAsync(db, Travas.Admins, ct);

            if (await db.Usuarios.AnyAsync(u => u.Role == Role.Admin, ct)) return ResultadoSeed.JaExiste;

            // Mesma regra do /auth/trocar-senha (12 a 128 caracteres, fora da lista de vazadas): sem isto o Admin nasceria com uma
            // senha que o próprio sistema recusaria na troca obrigatória. Recusa o boot (S-C02), sem repetir o valor.
            if (PoliticaDeSenha.Verificar(senha, vazadas) is { } motivo)
                throw new InvalidOperationException($"Seed:AdminSenha não passa na política de senha: {motivo}");

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

        // Sem e-mail nem senha no log; só o fato.
        if (resultado == ResultadoSeed.Criado)
            logger.LogWarning("ADMIN_SEMEADO: Admin inicial criado com DeveTrocarSenha");
        else if (resultado == ResultadoSeed.EmailEmUso)
            logger.LogError("ADMIN_SEED_CONFLITO: Seed__AdminEmail já pertence a uma conta que não é Admin; nenhum Admin foi criado");
    }

    private enum ResultadoSeed { JaExiste, EmailEmUso, Criado }
}
