using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Transmissao;

namespace WebRadio.Application.Features.Transmissao;

/// <summary><c>GET /locutores/me/credencial-transmissao</c>: nunca leva o segredo.</summary>
public sealed record CredencialDto(Guid Identificador, DateTime CriadaEmUtc, DateTime? UltimoUsoEmUtc);

/// <summary>Única resposta que contém o segredo (<c>POST</c>, com <c>Cache-Control: no-store</c>).</summary>
public sealed record CredencialGeradaDto(Guid Identificador, string Segredo, DateTime CriadaEmUtc)
{
    public override string ToString() => $"{nameof(CredencialGeradaDto)} {{ Identificador = {Identificador} }}";
}

public sealed record CredencialAtivaDto(Guid UsuarioId, string UsuarioNome, Guid Identificador, DateTime CriadaEmUtc, DateTime? UltimoUsoEmUtc);

public sealed record GerarCredencialCommand(Guid UsuarioId, string SenhaAtual) : IRequest<CredencialGeradaDto>
{
    public override string ToString() => nameof(GerarCredencialCommand);
}

/// <summary>
/// 06 "Geração": exige a senha da conta (um access token roubado não vira credencial persistente), 5 tentativas por hora por
/// usuário contando as com senha errada (senão a rota vira oráculo de senha), e gera uma nova revogando a anterior no mesmo
/// SaveChanges. Corrida entre duas gerações cai no índice de "uma ativa" e vira 409, nunca 500.
/// </summary>
public sealed class GerarCredencialHandler(
    IRadioDbContext db,
    ISenhaHasher hasher,
    IRequestThrottle throttle,
    IIpHasher pseudonimizador,
    IClock clock,
    ILogger<GerarCredencialHandler> logger) : IRequestHandler<GerarCredencialCommand, CredencialGeradaDto>
{
    public const int LimitePorHora = 5;
    public static readonly TimeSpan Janela = TimeSpan.FromHours(1);

    public async Task<CredencialGeradaDto> Handle(GerarCredencialCommand cmd, CancellationToken ct)
    {
        // Conta ANTES de qualquer verificação: a sexta tentativa da hora é 429 mesmo com a senha certa.
        await ContarTentativaAsync(cmd.UsuarioId, ct);

        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == cmd.UsuarioId && u.Ativo, ct)
            ?? throw new CredenciaisInvalidasException();
        if (usuario.Role is not (Role.Locutor or Role.Admin))
            throw new AcessoNegadoException("Só Locutor e Admin têm credencial de transmissão.");
        if (hasher.Verificar(usuario.SenhaHash, cmd.SenhaAtual) == ResultadoSenha.Falhou)
        {
            logger.LogWarning("CREDENCIAL_TRANSMISSAO_SENHA_INCORRETA usuario {UsuarioId}", usuario.Id);
            throw new CredenciaisInvalidasException();
        }

        var agora = clock.UtcNow;
        var anterior = await db.CredenciaisTransmissao.FirstOrDefaultAsync(c => c.UsuarioId == usuario.Id && c.RevogadaEmUtc == null, ct);
        anterior?.Revogar(agora);

        var gerado = SegredoDeTransmissao.Gerar();
        var nova = new CredencialTransmissao(Guid.NewGuid(), usuario.Id, SegredoDeTransmissao.NovoIdentificador(), gerado.Hash, gerado.Salt, agora);
        db.CredenciaisTransmissao.Add(nova);
        try
        {
            // O EF ordena o UPDATE da anterior antes do INSERT (o índice único é conhecido do modelo): uma transação só.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Outra geração gravou primeiro e o índice de "uma ativa" recusou esta. Qualquer outra falha segue como 500.
            if (await db.CredenciaisTransmissao.AsNoTracking()
                    .AnyAsync(c => c.UsuarioId == usuario.Id && c.RevogadaEmUtc == null && c.Id != nova.Id, ct))
                throw new GeracaoConcorrenteException();
            throw;
        }

        logger.LogInformation(
            "CREDENCIAL_TRANSMISSAO_GERADA usuario {UsuarioId} identificador {Identificador} revogou {IdentificadorAnterior}",
            usuario.Id, nova.Identificador, anterior?.Identificador);
        return new CredencialGeradaDto(nova.Identificador, gerado.Segredo, agora);
    }

    private async Task ContarTentativaAsync(Guid usuarioId, CancellationToken ct)
    {
        var chave = $"wr:credtx:{{{pseudonimizador.ChaveOpaca("usuario:" + usuarioId)}}}:geracoes";
        TimeSpan? resta;
        try
        {
            if (await throttle.IncrementarAsync(chave, Janela, ct) <= LimitePorHora) return;
            resta = await throttle.TempoRestanteAsync(chave, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sem o contador não há limite: recusa (503) em vez de liberar o oráculo de senha.
            logger.LogError(ex, "CREDENCIAL_TRANSMISSAO_LIMITE_INDISPONIVEL usuario {UsuarioId}", usuarioId);
            throw new ServidorOcupadoException();
        }

        throw new LimiteExcedidoException(resta is { } r && r > TimeSpan.Zero ? r : Janela);
    }
}

public sealed record ObterMinhaCredencialQuery(Guid UsuarioId) : IRequest<CredencialDto?>;

public sealed class ObterMinhaCredencialHandler(IRadioDbContext db) : IRequestHandler<ObterMinhaCredencialQuery, CredencialDto?>
{
    public Task<CredencialDto?> Handle(ObterMinhaCredencialQuery q, CancellationToken ct) =>
        db.CredenciaisTransmissao.AsNoTracking()
            .Where(c => c.UsuarioId == q.UsuarioId && c.RevogadaEmUtc == null)
            .Select(c => new CredencialDto(c.Identificador, c.CriadaEmUtc, c.UltimoUsoEmUtc))
            .FirstOrDefaultAsync(ct);
}

/// <summary><c>SolicitanteId</c> é quem revogou (o próprio locutor ou um Admin): vai para a auditoria.</summary>
public sealed record RevogarCredencialCommand(Guid UsuarioId, Guid SolicitanteId) : IRequest;

/// <summary>Idempotente: sem credencial ativa, 204 do mesmo jeito. Usuário inexistente → 404.</summary>
public sealed class RevogarCredencialHandler(IRadioDbContext db, IClock clock, ILogger<RevogarCredencialHandler> logger)
    : IRequestHandler<RevogarCredencialCommand>
{
    public async Task Handle(RevogarCredencialCommand cmd, CancellationToken ct)
    {
        if (!await db.Usuarios.AnyAsync(u => u.Id == cmd.UsuarioId, ct)) throw new NaoEncontradoException("Usuário");

        var ativa = await db.CredenciaisTransmissao.FirstOrDefaultAsync(c => c.UsuarioId == cmd.UsuarioId && c.RevogadaEmUtc == null, ct);
        if (ativa is null) return;

        ativa.Revogar(clock.UtcNow);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("CREDENCIAL_TRANSMISSAO_REVOGADA usuario {UsuarioId} identificador {Identificador} por {SolicitanteId}",
            cmd.UsuarioId, ativa.Identificador, cmd.SolicitanteId);
    }
}

public sealed record ListarCredenciaisAtivasQuery : IRequest<IReadOnlyList<CredencialAtivaDto>>;

public sealed class ListarCredenciaisAtivasHandler(IRadioDbContext db) : IRequestHandler<ListarCredenciaisAtivasQuery, IReadOnlyList<CredencialAtivaDto>>
{
    public async Task<IReadOnlyList<CredencialAtivaDto>> Handle(ListarCredenciaisAtivasQuery q, CancellationToken ct) =>
        await (from c in db.CredenciaisTransmissao.AsNoTracking()
               join u in db.Usuarios.AsNoTracking() on c.UsuarioId equals u.Id
               where c.RevogadaEmUtc == null
               orderby u.Nome
               select new CredencialAtivaDto(u.Id, u.Nome, c.Identificador, c.CriadaEmUtc, c.UltimoUsoEmUtc))
            .ToListAsync(ct);
}
