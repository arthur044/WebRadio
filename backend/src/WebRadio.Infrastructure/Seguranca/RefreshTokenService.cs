using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Autenticacao;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// S-M10. A rotação é um UPDATE condicional (RevogadoEmUtc IS NULL): zero linhas afetadas = outro request já consumiu o
/// token. Dentro da janela de 10 s o sucessor é devolvido de novo; como o banco guarda só o hash, o valor do sucessor é
/// derivado do hash do pai com HKDF-SHA256 (chave = pepper, info = "refresh-successor"): reproduzível por quem tem o
/// pepper, inalcançável sem ele. Depois da janela, o reuso revoga a família inteira (sinal de roubo).
/// </summary>
public sealed class RefreshTokenService(
    RadioDbContext db,
    IClock clock,
    IEmissorDeTokens tokens,
    byte[] pepper,
    ILogger<RefreshTokenService> logger) : IRefreshTokenService
{
    public static readonly TimeSpan JanelaDeReuso = TimeSpan.FromSeconds(10);

    public async Task<ResultadoRotacao> RotacionarAsync(string tokenBruto, byte[] ipHash, CancellationToken ct)
    {
        var hash = RefreshOpaco.HashDe(tokenBruto);
        if (hash is null) return ResultadoRotacao.Invalido;

        var estrategia = db.Database.CreateExecutionStrategy();

        // O bloco inteiro pode ser reexecutado no retry: sem efeito fora do banco.
        return await estrategia.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var agora = clock.UtcNow;

            var reivindicado = await db.RefreshTokens
                .Where(t => t.TokenHash == hash && t.RevogadoEmUtc == null && t.ExpiraEmUtc > agora)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevogadoEmUtc, agora), ct);

            var atual = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
            if (atual is null) return ResultadoRotacao.Invalido;

            ResultadoRotacao resultado;
            if (reivindicado == 1) resultado = await CriarSucessorAsync(atual, hash, ipHash, agora, ct);
            else if (atual.RevogadoEmUtc is null) resultado = ResultadoRotacao.Invalido; // expirado, não reusado
            else resultado = await TratarTokenRevogadoAsync(atual, hash, agora, ct);

            await tx.CommitAsync(ct);
            return resultado;
        });
    }

    public async Task RevogarFamiliaAsync(string tokenBruto, CancellationToken ct)
    {
        var hash = RefreshOpaco.HashDe(tokenBruto);
        if (hash is null) return;

        var familia = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => (Guid?)t.FamiliaId)
            .FirstOrDefaultAsync(ct);

        if (familia is not null) await RevogarFamiliaInternaAsync(familia.Value, clock.UtcNow, ct);
    }

    public async Task RevogarTodasDoUsuarioAsync(Guid usuarioId, string? manterFamiliaDoToken, CancellationToken ct)
    {
        Guid? manter = null;
        var hashAtual = RefreshOpaco.HashDe(manterFamiliaDoToken);
        if (hashAtual is not null)
        {
            manter = await db.RefreshTokens.AsNoTracking()
                .Where(t => t.TokenHash == hashAtual && t.UsuarioId == usuarioId)
                .Select(t => (Guid?)t.FamiliaId)
                .FirstOrDefaultAsync(ct);
        }

        var agora = clock.UtcNow;
        await db.RefreshTokens
            .Where(t => t.UsuarioId == usuarioId && t.RevogadoEmUtc == null && (manter == null || t.FamiliaId != manter))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevogadoEmUtc, agora), ct);
    }

    private async Task<ResultadoRotacao> CriarSucessorAsync(RefreshToken pai, byte[] hashPai, byte[] ipHash, DateTime agora, CancellationToken ct)
    {
        var novo = DerivarSucessor(hashPai);
        var sucessor = new RefreshToken(Guid.CreateVersion7(), pai.UsuarioId, novo.Hash, pai.FamiliaId, agora, tokens.DuracaoDoRefresh, ipHash);

        db.RefreshTokens.Add(sucessor);
        await db.SaveChangesAsync(ct);

        await db.RefreshTokens
            .Where(t => t.Id == pai.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.SubstituidoPorId, sucessor.Id), ct);

        db.ChangeTracker.Clear();
        return new ResultadoRotacao(true, pai.UsuarioId, novo);
    }

    private async Task<ResultadoRotacao> TratarTokenRevogadoAsync(RefreshToken revogado, byte[] hash, DateTime agora, CancellationToken ct)
    {
        // Janela de tolerância (duas abas, retry de rede): devolve o mesmo sucessor se ele ainda estiver vivo.
        if (revogado.SubstituidoPorId is { } sucessorId && agora - revogado.RevogadoEmUtc <= JanelaDeReuso)
        {
            var sucessor = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == sucessorId, ct);
            if (sucessor is { EstaRevogado: false }) return new ResultadoRotacao(true, revogado.UsuarioId, DerivarSucessor(hash));

            // Sucessor JÁ ROTACIONADO (tem filho) por outra aba dentro da janela: uso legítimo concorrente, não roubo.
            // Revogado SEM filho = logout/troca de senha/desativação: sessão morta, não corrida (cai na revogação abaixo).
            if (sucessor is { SubstituidoPorId: not null }) return ResultadoRotacao.PerdeuCorrida;
        }

        // Rotacionado e reapresentado fora da janela = provável roubo. Sem sucessor (revogado por logout/segurança) é só
        // sessão encerrada: o efeito é o mesmo (família revogada), o log não acusa roubo.
        if (revogado.SubstituidoPorId is not null)
            logger.LogWarning("Reuso de refresh token já rotacionado; família {FamiliaId} revogada", revogado.FamiliaId);
        else
            logger.LogInformation("Refresh token revogado reapresentado (logout/troca de senha); família {FamiliaId}", revogado.FamiliaId);
        await RevogarFamiliaInternaAsync(revogado.FamiliaId, agora, ct);
        return ResultadoRotacao.Invalido;
    }

    private Task RevogarFamiliaInternaAsync(Guid familiaId, DateTime agora, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.FamiliaId == familiaId && t.RevogadoEmUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevogadoEmUtc, agora), ct);

    private RefreshOpaco DerivarSucessor(byte[] hashPai) =>
        RefreshOpaco.De(HKDF.DeriveKey(HashAlgorithmName.SHA256, pepper, RefreshOpaco.TamanhoEmBytes, salt: hashPai, info: "refresh-successor"u8.ToArray()));
}
