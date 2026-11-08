using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Comum;
using WebRadio.Application.Features.Pedidos;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Midia;

namespace WebRadio.Application.Features.Midias;

/// <summary><c>upload</c> do 201 de <c>POST /midias/uploads</c> (02 §6): o browser faz <c>multipart/form-data</c> para <c>url</c>, com os <c>campos</c> e o <c>file</c> por último.</summary>
public sealed record UploadDto(string Url, IReadOnlyDictionary<string, string> Campos, DateTime ExpiraEmUtc);

public sealed record UploadIniciadoDto(Guid MidiaId, UploadDto Upload);

/// <summary>
/// <c>POST /midias/uploads</c> (02 §6, 08 E4-F02). <c>TamanhoBytes</c> é o do arquivo: vira o teto da POST policy (o storage recusa mais que isso) e é
/// conferido no <c>concluir</c>. O nome do arquivo é do cliente: o <c>ToString</c> não o imprime.
/// </summary>
public sealed record IniciarUploadCommand(
    Guid UsuarioId,
    string NomeOriginal,
    string? Titulo,
    string? Artista,
    TipoMidia TipoMidia,
    string MimeTypeDeclarado,
    long TamanhoBytes) : IRequest<UploadIniciadoDto>
{
    public override string ToString() => nameof(IniciarUploadCommand);
}

public sealed class IniciarUploadValidator : AbstractValidator<IniciarUploadCommand>
{
    public const int TamanhoNomeOriginal = 255;
    public const int TamanhoTitulo = 200;
    public const int TamanhoArtista = 200;

    /// <summary>A lista fechada do 02 §6: entra como condição <c>eq</c> da policy, então tem de ser o tipo exato (sem parâmetros).</summary>
    public static readonly IReadOnlySet<string> MimesDeclaraveis = new HashSet<string>(StringComparer.Ordinal)
    {
        "audio/mpeg", "audio/ogg", "audio/wav", "audio/x-wav", "audio/flac",
    };

    private static readonly char[] SeparadoresProibidos = ['/', '\\', ':'];

    public IniciarUploadValidator(MidiaOptions opcoes)
    {
        RuleFor(x => x.TipoMidia).IsInEnum();
        // S-M07: o nome vai ao Content-Disposition e ao log; barra e dois-pontos abririam caminho. TextoSeguro já recusa controle, formato e bidi.
        RuleFor(x => x.NomeOriginal).TextoSeguro(TamanhoNomeOriginal)
            .Must(n => n is null || n.IndexOfAny(SeparadoresProibidos) < 0).WithMessage("O nome do arquivo não pode ter / \\ nem :.");
        When(x => x.TipoMidia == TipoMidia.Musica || x.Titulo is { Length: > 0 }, () => RuleFor(x => x.Titulo).TextoSeguro(TamanhoTitulo));
        When(x => x.Artista is { Length: > 0 }, () => RuleFor(x => x.Artista).TextoSeguro(TamanhoArtista));
        RuleFor(x => x.MimeTypeDeclarado).Must(m => m is not null && MimesDeclaraveis.Contains(m))
            .WithMessage("mimeTypeDeclarado precisa ser audio/mpeg, audio/ogg, audio/wav, audio/x-wav ou audio/flac.");
        RuleFor(x => x.TamanhoBytes).GreaterThan(0).WithMessage("tamanhoBytes precisa ser positivo.");
        // Antes da policy (08 E4-F02): o tipo desconhecido já caiu na regra do enum e não deve estourar aqui.
        When(x => Enum.IsDefined(x.TipoMidia), () =>
            RuleFor(x => x.TamanhoBytes).Must((x, t) => t <= opcoes.Limite(x.TipoMidia).TamanhoMaximoBytes)
                .WithMessage(x => $"tamanhoBytes acima do limite de {opcoes.Limite(x.TipoMidia).TamanhoMaximoBytes} bytes para {x.TipoMidia}."));
    }
}

/// <summary>
/// Limite de 20 uploads por hora por usuário (S-M06), janela fixa no Redis com o disjuntor do throttle de pedidos: com o Redis fora, devolve
/// <c>true</c> e o handler conta no SQL, dentro da transação do insert. A chave leva só o HMAC do id.
/// </summary>
public sealed class LimiteDeUploadDeMidia(IRequestThrottle contadores, IIpHasher hasher, IClock clock, IOptions<MidiaOptions> opcoes, ILogger<LimiteDeUploadDeMidia> logger)
{
    public static readonly TimeSpan Janela = TimeSpan.FromHours(1);

    private readonly DisjuntorDoRedis _disjuntor = new(clock);

    public static string ChaveOpaca(IIpHasher hasher, Guid usuario) => hasher.ChaveOpaca("midiaup:" + usuario.ToString("N"));

    /// <summary>Conta a tentativa. Lança <see cref="LimiteExcedidoException"/> acima do limite; <c>true</c> = o Redis não respondeu (fallback SQL).</summary>
    public async Task<bool> RegistrarAsync(Guid usuario, CancellationToken ct)
    {
        var permissao = _disjuntor.Pedir();
        if (permissao == PermissaoDoRedis.Evitar) return true;

        var chave = $"throttle:midiaup:{ChaveOpaca(hasher, usuario)}";
        long n;
        try
        {
            n = await contadores.IncrementarAsync(chave, Janela, ct);
        }
        catch (OperationCanceledException)
        {
            _disjuntor.Cancelada(permissao);
            throw;
        }
        catch (Exception e)
        {
            if (_disjuntor.Falha(permissao))
                logger.LogWarning("MIDIA_UPLOAD_REDIS_INDISPONIVEL ({Tipo}): limite de 20/h no SQL por {Segundos}s (modo degradado)", e.GetType().Name, DisjuntorDoRedis.TempoAberto.TotalSeconds);
            return true;
        }

        _disjuntor.Sucesso(permissao);
        if (n <= opcoes.Value.UploadsPorHora) return false;
        var restante = await contadores.TempoRestanteAsync(chave, ct);
        throw new LimiteExcedidoException(restante is { } r && r > TimeSpan.Zero ? r : Janela);
    }
}

/// <summary>
/// 08 E4-F02: policy primeiro (sem ela não há registro), depois, na MESMA transação e sob a trava do usuário, a contagem dos abertos
/// (<c>AguardandoUpload</c>, <c>Pendente</c>, <c>EmAnalise</c>) e o <c>INSERT</c>. Sem a trava, 20 <c>POST</c> paralelos passariam do limite de 5.
/// </summary>
public sealed class IniciarUploadHandler(
    IRadioDbContext db,
    ITransacaoComTrava transacao,
    IClock clock,
    IIpHasher hasher,
    LimiteDeUploadDeMidia limite,
    IOptions<MidiaOptions> opcoes,
    IObjectStorage? storage = null) : IRequestHandler<IniciarUploadCommand, UploadIniciadoDto>
{
    public static readonly TimeSpan EsperaDaTrava = TimeSpan.FromMilliseconds(2000);
    /// <summary>15 min (S-M02): é a janela em que o POST na mesma chave ainda é aceito. A validade é checada quando o POST CHEGA ao storage, então um upload lento que já começou termina.</summary>
    public static readonly TimeSpan ValidadeDaPolicy = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan EsperaComAbertosNoLimite = TimeSpan.FromSeconds(60);

    private static readonly StatusSanitizacao[] Abertos = [StatusSanitizacao.AguardandoUpload, StatusSanitizacao.Pendente, StatusSanitizacao.EmAnalise];

    public async Task<UploadIniciadoDto> Handle(IniciarUploadCommand cmd, CancellationToken ct)
    {
        var destino = storage ?? throw new InvalidOperationException("Storage não configurado.");
        var usarSql = await limite.RegistrarAsync(cmd.UsuarioId, ct);
        var o = opcoes.Value;
        var agora = clock.UtcNow;

        var midia = new ArquivoMidia(
            Guid.CreateVersion7(), cmd.NomeOriginal, Normalizar(cmd.Titulo), Normalizar(cmd.Artista), cmd.TipoMidia,
            cmd.MimeTypeDeclarado, cmd.TamanhoBytes, cmd.UsuarioId, agora);
        var policy = await destino.CriarPostPolicyAsync(Buckets.Quarentena, midia.ChaveStorage, cmd.MimeTypeDeclarado, cmd.TamanhoBytes, ValidadeDaPolicy, ct);

        var trava = Travas.UploadDeMidiaDoUsuario(LimiteDeUploadDeMidia.ChaveOpaca(hasher, cmd.UsuarioId));
        try
        {
            await transacao.ExecutarAsync([trava], EsperaDaTrava, async ct2 =>
            {
                var abertos = await db.ArquivosMidia.CountAsync(m => m.EnviadoPorUsuarioId == cmd.UsuarioId && Abertos.Contains(m.StatusSanitizacao), ct2);
                if (abertos >= o.MaximoAbertos) throw new LimiteExcedidoException(EsperaComAbertosNoLimite);

                if (usarSql)
                {
                    var corte = agora - LimiteDeUploadDeMidia.Janela;
                    var datas = await db.ArquivosMidia.Where(m => m.EnviadoPorUsuarioId == cmd.UsuarioId && m.DataUploadUtc > corte)
                        .OrderBy(m => m.DataUploadUtc).Select(m => m.DataUploadUtc).ToListAsync(ct2);
                    if (datas.Count >= o.UploadsPorHora)
                        throw new LimiteExcedidoException(TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, (datas[datas.Count - o.UploadsPorHora] + LimiteDeUploadDeMidia.Janela - agora).Ticks)));
                }

                db.ArquivosMidia.Add(midia);
                await db.SaveChangesAsync(ct2);
                return true;
            }, ct);
        }
        catch (TravaOcupadaException)
        {
            throw new LimiteExcedidoException(TimeSpan.FromSeconds(1));
        }

        return new UploadIniciadoDto(midia.Id, new UploadDto(policy.Url, policy.Campos, policy.ExpiraEmUtc));
    }

    private static string? Normalizar(string? texto) => string.IsNullOrEmpty(texto) ? null : TextoSeguro.Normalizar(texto);
}

/// <summary><c>POST /midias/{id}/concluir</c>: o dono (ou o Admin) avisa que o upload terminou. <c>EhAdmin</c> vem do papel do token.</summary>
public sealed record ConcluirUploadCommand(Guid MidiaId, Guid UsuarioId, bool EhAdmin) : IRequest;

/// <summary>
/// HEAD no objeto da quarentena: ausente ou de tamanho diferente do declarado → 400 e o registro continua <c>AguardandoUpload</c> até expirar (02 §6).
/// Guarda o <c>ETag</c> (sem aspas) para o <c>GET</c> condicional do Worker (S-M02). Mídia de outro usuário é 404 (não revela que existe).
/// </summary>
public sealed class ConcluirUploadHandler(IRadioDbContext db, IClock clock, IObjectStorage? storage = null) : IRequestHandler<ConcluirUploadCommand>
{
    public async Task Handle(ConcluirUploadCommand cmd, CancellationToken ct)
    {
        var destino = storage ?? throw new InvalidOperationException("Storage não configurado.");
        var midia = await db.ArquivosMidia.FirstOrDefaultAsync(m => m.Id == cmd.MidiaId, ct);
        if (midia is null || (midia.EnviadoPorUsuarioId != cmd.UsuarioId && !cmd.EhAdmin)) throw new NaoEncontradoException("Mídia");

        var info = await destino.HeadAsync(midia.Bucket, midia.ChaveStorage, ct)
            ?? throw Invalido("O arquivo ainda não chegou ao storage. Envie o arquivo antes de concluir.");
        if (info.TamanhoBytes != midia.TamanhoBytes) throw Invalido("O tamanho do arquivo enviado é diferente do declarado.");
        var etag = info.ETag?.Trim('"');
        if (string.IsNullOrEmpty(etag)) throw Invalido("O storage não devolveu o ETag do arquivo.");

        midia.ConcluirUpload(info.TamanhoBytes, etag, clock.UtcNow);
        await db.SaveChangesAsync(ct);
    }

    private static ValidationException Invalido(string mensagem) => new([new FluentValidation.Results.ValidationFailure("midiaId", mensagem)]);
}
