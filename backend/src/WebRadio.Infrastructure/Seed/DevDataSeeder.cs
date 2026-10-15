using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Programas;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Interacao;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Seed;

/// <summary>
/// E1-A06 + E2-A03: dados de DESENVOLVIMENTO. Roda no boot da Api, depois do migrator (as tabelas já existem), e cria os 2 locutores
/// fictícios, uma semana de grade e 3 divulgações. A grade entra pelo <see cref="CriarProgramaCommand"/> da E2-F01, então respeita
/// as regras de horário e a trava de sobreposição como qualquer programa criado pela API. A Api só registra este seeder em
/// Development e com <c>Seed:DevData=true</c>; a senha vem de <c>Seed:DevSenha</c> (arquivo de segredo: o repositório é público).
/// Substitui o <c>infra/sql/seed-dev.sql</c> do db-init, que rodava ANTES do migrator, sem as tabelas.
/// </summary>
public sealed class DevDataSeeder(RadioDbContext db, ISenhaHasher hasher, IClock clock, IConfiguration config, IHostEnvironment ambiente,
    ILogger<DevDataSeeder> logger, IMediator mediator) : IInicializadorDeDados
{
    public static readonly IReadOnlyList<(string Nome, string Email)> Locutores =
    [
        ("Locutor Um (seed)", "locutor1@example.com"),
        ("Locutor Dois (seed)", "locutor2@example.com"),
    ];

    /// <summary>Título dos itens do seed: é o que identifica "já semeado" e o que separa o seed do resto.</summary>
    public const string Prefixo = "[seed] ";

    private const int DiasDeGrade = 7;

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

        var ids = new List<Guid>();
        foreach (var (_, email) in Locutores)
        {
            var normalizado = email.ToUpperInvariant();
            ids.Add(await db.Usuarios.Where(u => u.EmailNormalizado == normalizado).Select(u => u.Id).SingleAsync(ct));
        }

        await SemearGradeAsync(ids, ct);
        await SemearDivulgacoesAsync(ids[0], ids[1], ct);
    }

    /// <summary>
    /// 7 dias a partir de amanhã, das 12:00 às 14:00 UTC (9h–11h em Brasília), alternando os locutores. Só roda se ainda não houver
    /// programa do seed; um dia que conflita com outro programa é pulado, sem derrubar o boot.
    /// </summary>
    private async Task SemearGradeAsync(IReadOnlyList<Guid> locutores, CancellationToken ct)
    {
        if (await db.Programas.AnyAsync(p => p.Titulo.StartsWith(Prefixo), ct)) return;

        var amanha = clock.UtcNow.Date.AddDays(1);
        var criados = 0;
        for (var dia = 0; dia < DiasDeGrade; dia++)
        {
            var inicio = amanha.AddDays(dia).AddHours(12);
            try
            {
                await mediator.Send(new CriarProgramaCommand($"{Prefixo}Programa Dia {dia + 1}",
                    "Programa de exemplo gerado pelo DevDataSeeder — dados fictícios.", locutores[dia % 2], inicio, inicio.AddHours(2)), ct);
                criados++;
            }
            catch (ConflitoDeGradeException)
            {
                logger.LogInformation("DEV_SEED: dia {Dia} da grade pulado por conflito com outro programa", dia + 1);
            }
        }

        logger.LogInformation("DEV_SEED: {Criados} programa(s) de exemplo criados na grade", criados);
    }

    private async Task SemearDivulgacoesAsync(Guid locutor1, Guid locutor2, CancellationToken ct)
    {
        if (await db.Divulgacoes.AnyAsync(d => d.Titulo.StartsWith(Prefixo), ct)) return;

        var agora = clock.UtcNow;
        db.Divulgacoes.AddRange(
            new Divulgacao(Guid.CreateVersion7(), $"{Prefixo}Bem-vindo à WebRadio", "Ambiente de desenvolvimento — dados fictícios, sem relação com pessoas reais.", null, null, 80, null, null, locutor1, agora),
            new Divulgacao(Guid.CreateVersion7(), $"{Prefixo}Peça sua música", "Peça sua música favorita direto pelo site, sem precisar de cadastro.", null, null, 50, null, null, locutor1, agora),
            new Divulgacao(Guid.CreateVersion7(), $"{Prefixo}Programação da semana", "Confira a grade da semana na aba Grade.", null, null, 30, null, null, locutor2, agora));
        await db.SaveChangesAsync(ct);
        logger.LogInformation("DEV_SEED: 3 divulgações de exemplo criadas");
    }
}
