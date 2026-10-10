using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Autenticacao;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Opcoes;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// S-M10. A rotação é um único UPDATE condicional (RevogadoEmUtc IS NULL): zero linhas afetadas = outro request já consumiu o
/// token. Dentro da janela de 10 s o sucessor é devolvido de novo; como o banco guarda só o hash, o token bruto do sucessor é
/// derivado do hash do pai com HMAC(pepper), o que o torna reproduzível sem ser adivinhável. Depois da janela, o reuso
/// revoga a família inteira (sinal de roubo).
/// </summary>
public sealed class RefreshTokenService(
    RadioDbContext db,
    IClock clock,
    IOptions<JwtOptions> jwt,
    IOptions<SegurancaOptions> seguranca,
    ILogger<RefreshTokenService> logger) : IRefreshTokenService
{
    public static readonly TimeSpan JanelaDeReuso = TimeSpan.FromSeconds(10);
    private const int TamanhoTokenBytes = 32;

    private readonly byte[] _pepper = seguranca.Value.PepperBytes();

    private TimeSpan Duracao => TimeSpan.FromDays(jwt.Value.RefreshTokenDias);

    public async Task<RefreshEmitido> EmitirAsync(Usuario usuario, byte[]? ipHash, CancellationToken cancellationToken = default)
    {
        var bruto = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TamanhoTokenBytes));
        var token = Criar(usuario.Id, Guid.CreateVersion7(), bruto, ipHash, clock.UtcAgora);

        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);
        return new RefreshEmitido(bruto, token.ExpiraEmUtc);
    }

    public async Task<ResultadoRotacao> RotacionarAsync(string tokenBruto, byte[]? ipHash, CancellationToken cancellationToken = default)
    {
        if (!FormatoValido(tokenBruto))
        {
            return ResultadoRotacao.Invalido;
        }

        var hash = Hash(tokenBruto);
        var estrategia = db.Database.CreateExecutionStrategy();

        // O bloco inteiro pode ser reexecutado no retry: sem efeito fora do banco.
        return await estrategia.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            var agora = clock.UtcAgora;

            var reivindicado = await db.RefreshTokens
                .Where(t => t.TokenHash == hash && t.RevogadoEmUtc == null && t.ExpiraEmUtc > agora)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevogadoEmUtc, agora), cancellationToken);

            var atual = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
            if (atual is null)
            {
                return ResultadoRotacao.Invalido;
            }

            ResultadoRotacao resultado;
            if (reivindicado == 1)
            {
                resultado = await CriarSucessorAsync(atual, hash, ipHash, agora, cancellationToken);
            }
            else if (atual.RevogadoEmUtc is null)
            {
                resultado = ResultadoRotacao.Invalido; // expirado, não reusado: nada a revogar.
            }
            else
            {
                resultado = await TratarTokenRevogadoAsync(atual, hash, agora, cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return resultado;
        });
    }

    public async Task RevogarFamiliaAsync(string tokenBruto, CancellationToken cancellationToken = default)
    {
        if (!FormatoValido(tokenBruto))
        {
            return;
        }

        var hash = Hash(tokenBruto);
        var familia = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => (Guid?)t.FamiliaId)
            .FirstOrDefaultAsync(cancellationToken);

        if (familia is not null)
        {
            await RevogarFamiliaInternaAsync(familia.Value, clock.UtcAgora, cancellationToken);
        }
    }

    public async Task RevogarTodasDoUsuarioAsync(Guid usuarioId, string? manterFamiliaDoToken, CancellationToken cancellationToken = default)
    {
        Guid? manter = null;
        if (manterFamiliaDoToken is not null && FormatoValido(manterFamiliaDoToken))
        {
            var hash = Hash(manterFamiliaDoToken);
            manter = await db.RefreshTokens.AsNoTracking()
                .Where(t => t.TokenHash == hash && t.UsuarioId == usuarioId)
                .Select(t => (Guid?)t.FamiliaId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var agora = clock.UtcAgora;
        await db.RefreshTokens
            .Where(t => t.UsuarioId == usuarioId && t.RevogadoEmUtc == null && (manter == null || t.FamiliaId != manter))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevogadoEmUtc, agora), cancellationToken);
    }

    private async Task<ResultadoRotacao> CriarSucessorAsync(RefreshToken pai, byte[] hashPai, byte[]? ipHash, DateTime agora, CancellationToken ct)
    {
        var bruto = DerivarSucessor(hashPai);
        var sucessor = Criar(pai.UsuarioId, pai.FamiliaId, bruto, ipHash, agora);

        db.RefreshTokens.Add(sucessor);
        await db.SaveChangesAsync(ct);

        await db.RefreshTokens
            .Where(t => t.Id == pai.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.SubstituidoPorId, sucessor.Id), ct);

        db.ChangeTracker.Clear();
        return new ResultadoRotacao(true, pai.UsuarioId, new RefreshEmitido(bruto, sucessor.ExpiraEmUtc));
    }

    private async Task<ResultadoRotacao> TratarTokenRevogadoAsync(RefreshToken revogado, byte[] hash, DateTime agora, CancellationToken ct)
    {
        // Janela de tolerância (duas abas, retry de rede): devolve o mesmo sucessor se ele ainda estiver vivo.
        if (revogado.SubstituidoPorId is { } sucessorId && agora - revogado.RevogadoEmUtc <= JanelaDeReuso)
        {
            var sucessor = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == sucessorId, ct);
            if (sucessor is { EstaRevogado: false })
            {
                return new ResultadoRotacao(true, revogado.UsuarioId, new RefreshEmitido(DerivarSucessor(hash), sucessor.ExpiraEmUtc));
            }

            // Sucessor ja rotacionado por outra aba dentro da janela: uso legitimo concorrente, nao roubo. Esta aba apenas
            // recusa (o cliente refaz o refresh com o cookie novo); revogar a familia derrubaria todas as sessoes.
            if (sucessor is not null)
            {
                return ResultadoRotacao.PerdeuCorrida;
            }
        }

        // Reuso fora da janela (ou token revogado por logout/segurança): a família toda cai.
        logger.LogWarning("REUSO_REFRESH: token revogado reapresentado; família {FamiliaId} revogada", revogado.FamiliaId);
        await RevogarFamiliaInternaAsync(revogado.FamiliaId, agora, ct);
        return ResultadoRotacao.Invalido;
    }

    private Task RevogarFamiliaInternaAsync(Guid familiaId, DateTime agora, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.FamiliaId == familiaId && t.RevogadoEmUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevogadoEmUtc, agora), ct);

    private RefreshToken Criar(Guid usuarioId, Guid familiaId, string bruto, byte[]? ipHash, DateTime agora) =>
        new(Guid.CreateVersion7(), usuarioId, Hash(bruto), familiaId, agora, Duracao, ipHash);

    private string DerivarSucessor(byte[] hashPai)
    {
        var rotulo = "refresh-sucessor:"u8;
        var entrada = new byte[rotulo.Length + hashPai.Length];
        rotulo.CopyTo(entrada);
        hashPai.CopyTo(entrada, rotulo.Length);
        return Base64Url.EncodeToString(HMACSHA256.HashData(_pepper, entrada));
    }

    /// <summary>SHA-256 do texto do token; a busca é pelo índice único, nunca por == em memória (05 §6).</summary>
    public static byte[] Hash(string tokenBruto) => SHA256.HashData(Encoding.UTF8.GetBytes(tokenBruto));

    private static bool FormatoValido(string token) =>
        token.Length == Base64Url.GetEncodedLength(TamanhoTokenBytes) && Base64Url.IsValid(token);
}
