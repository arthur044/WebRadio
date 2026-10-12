using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Seed;

/// <summary>
/// E1-A06: cria os 2 locutores fictícios que o infra/sql/seed-dev.sql procura por EmailNormalizado. A Api só registra este
/// seeder em Development e com Seed:DevData=true; a senha vem de Seed:DevSenha (nada hardcoded: o repositório é público).
/// </summary>
public sealed class DevDataSeeder(RadioDbContext db, ISenhaHasher hasher, IClock clock, IConfiguration config, IHostEnvironment ambiente, ILogger<DevDataSeeder> logger)
    : IInicializadorDeDados
{
    public static readonly IReadOnlyList<(string Nome, string Email)> Locutores =
    [
        ("Locutor Um (seed)", "locutor1@example.com"),
        ("Locutor Dois (seed)", "locutor2@example.com"),
    ];

    public async Task ExecutarAsync(CancellationToken ct)
    {
        if (!config.GetValue<bool>("Seed:DevData")) return;

        // Defesa em profundidade: a Api só registra este seeder em Development, mas qualquer outro host que o registre
        // criaria contas com senha conhecida. Fora de Development, falha alto em vez de seguir.
        if (!ambiente.IsDevelopment())
            throw new InvalidOperationException("Seed:DevData só é permitido em Development.");

        var senha = config["Seed:DevSenha"] ?? throw new InvalidOperationException("Seed:DevSenha é obrigatório com Seed:DevData=true.");
        foreach (var (nome, email) in Locutores)
        {
            var normalizado = email.ToUpperInvariant();
            if (await db.Usuarios.AnyAsync(u => u.EmailNormalizado == normalizado, ct)) continue;
            db.Usuarios.Add(new Usuario(Guid.CreateVersion7(), nome, email, hasher.Hash(senha), Role.Locutor, deveTrocarSenha: false, clock.UtcNow));
        }

        if (await db.SaveChangesAsync(ct) > 0) logger.LogInformation("DEV_SEED: locutores fictícios criados");
    }
}
