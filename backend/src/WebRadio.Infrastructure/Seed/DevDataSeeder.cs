using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Opcoes;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Seed;

/// <summary>
/// E1-A06: cria os 2 locutores fictícios que o infra/sql/seed-dev.sql procura por EmailNormalizado. Só roda com
/// IsDevelopment() E Seed:DevData=true; a senha vem de Seed:DevSenha (nada hardcoded: o repositório é público).
/// </summary>
public sealed class DevDataSeeder(
    RadioDbContext db,
    IPasswordHasher hasher,
    IClock clock,
    IHostEnvironment ambiente,
    IOptions<SeedOptions> opcoes,
    ILogger<DevDataSeeder> logger) : IInicializadorDeDados
{
    public static readonly IReadOnlyList<(string Nome, string Email)> Locutores =
    [
        ("Locutor Um (seed)", "locutor1@example.com"),
        ("Locutor Dois (seed)", "locutor2@example.com"),
    ];

    public async Task ExecutarAsync(CancellationToken cancellationToken = default)
    {
        if (!ambiente.IsDevelopment() || !opcoes.Value.DevData)
        {
            return;
        }

        foreach (var (nome, email) in Locutores)
        {
            var normalizado = email.ToUpperInvariant();
            if (await db.Usuarios.AnyAsync(u => u.EmailNormalizado == normalizado, cancellationToken))
            {
                continue;
            }

            db.Usuarios.Add(new Usuario(
                Guid.CreateVersion7(), nome, email, hasher.Hash(opcoes.Value.DevSenha), Role.Locutor, deveTrocarSenha: false, clock.UtcAgora));
        }

        if (await db.SaveChangesAsync(cancellationToken) > 0)
        {
            logger.LogInformation("DEV_SEED: locutores fictícios criados");
        }
    }
}
