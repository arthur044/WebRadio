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

    public static string ChaveDosBytes(IIpHasher hasher, Guid usuario) => hasher.ChaveOpaca("midiabytes:" + usuario.ToString("N"));

    /// <summary>
    /// Desfaz o que o <see cref="RegistrarAsync"/> somou, quando o POST foi RECUSADO depois dele (limite de abertos, trava ocupada, falha da assinatura): nenhum envio nasceu, então
    /// nem a tentativa (20/h) nem os bytes contam. Só o CANCELAMENTO de um envio que nasceu não devolve nada (08 v1.5). Melhor esforço: Redis fora = nada a desfazer.
    /// </summary>
    public async Task DesfazerAsync(Guid usuario, long tamanhoBytes, CancellationToken ct)
    {
        try
        {
            await contadores.IncrementarPorAsync($"throttle:midiaup:{ChaveOpaca(hasher, usuario)}", -1, Janela, ct);
            await contadores.IncrementarPorAsync($"throttle:{ChaveDosBytes(hasher, usuario)}", -tamanhoBytes, Janela, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("MIDIA_UPLOAD_CONTADORES_NAO_DEVOLVIDOS ({Tipo})", e.GetType().Name);
        }
    }

    /// <summary>
    /// Conta a tentativa (20/h) e soma os bytes declarados no orçamento por hora (08 v1.5). Lança <see cref="LimiteExcedidoException"/> acima de qualquer um dos dois; <c>true</c> = o Redis não
    /// respondeu (fallback SQL). Um <c>POST</c> recusado por bytes devolve os bytes que somou (só a tentativa conta); o CANCELAMENTO do envio nunca devolve nada.
    /// </summary>
    public async Task<bool> RegistrarAsync(Guid usuario, long tamanhoBytes, CancellationToken ct)
    {
        var permissao = _disjuntor.Pedir();
        if (permissao == PermissaoDoRedis.Evitar) return true;

        var chave = $"throttle:midiaup:{ChaveOpaca(hasher, usuario)}";
        var chaveBytes = $"throttle:{ChaveDosBytes(hasher, usuario)}";
        var o = opcoes.Value;
        long n;
        long? bytes = null;
        try
        {
            n = await contadores.IncrementarAsync(chave, Janela, ct);
            if (n <= o.UploadsPorHora) bytes = await contadores.IncrementarPorAsync(chaveBytes, tamanhoBytes, Janela, ct);
        }
        catch (OperationCanceledException)
        {
            _disjuntor.Cancelada(permissao);
            throw;
        }
        catch (Exception e)
        {
            if (_disjuntor.Falha(permissao))
                logger.LogWarning("MIDIA_UPLOAD_REDIS_INDISPONIVEL ({Tipo}): limites de 20/h e de bytes no SQL por {Segundos}s (modo degradado)", e.GetType().Name, DisjuntorDoRedis.TempoAberto.TotalSeconds);
            return true;
        }

        _disjuntor.Sucesso(permissao);
        if (n > o.UploadsPorHora)
        {
            var restante = await contadores.TempoRestanteAsync(chave, ct);
            throw new LimiteExcedidoException(restante is { } r && r > TimeSpan.Zero ? r : Janela);
        }

        if (bytes!.Value > o.BytesDeclaradosPorHora)
        {
            try
            {
                await contadores.IncrementarPorAsync(chaveBytes, -tamanhoBytes, Janela, ct); // a tentativa recusada não come o orçamento
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("MIDIA_UPLOAD_BYTES_NAO_DEVOLVIDOS ({Tipo})", e.GetType().Name);
            }

            var restante = await contadores.TempoRestanteAsync(chaveBytes, ct);
            throw new LimiteExcedidoException(restante is { } r && r > TimeSpan.Zero ? r : Janela);
        }

        return false;
    }
}

/// <summary>
/// 08 E4-F02: na MESMA transação e sob a trava do usuário, a contagem dos abertos (<c>AguardandoUpload</c> não vencido, <c>Pendente</c>, <c>EmAnalise</c>) e o
/// <c>INSERT</c>; sem a trava, 20 <c>POST</c> paralelos passariam do limite de 5. Nada fora do banco dentro do bloco (o EF o reexecuta num retry): o contador de 20/h
/// do Redis fica antes dele e a POST policy é assinada DEPOIS do commit (se a assinatura falhar, o registro é descartado).
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
    /// <summary><c>Retry-After</c> mínimo do limite de abertos, e o valor quando nenhum <c>AguardandoUpload</c> está aberto (só <c>Pendente</c>/<c>EmAnalise</c>: o Worker libera quando quiser).</summary>
    public static readonly TimeSpan EsperaComAbertosNoLimite = TimeSpan.FromSeconds(60);

    private static readonly StatusSanitizacao[] Abertos = [StatusSanitizacao.AguardandoUpload, StatusSanitizacao.Pendente, StatusSanitizacao.EmAnalise];

    public async Task<UploadIniciadoDto> Handle(IniciarUploadCommand cmd, CancellationToken ct)
    {
        var destino = storage ?? throw new InvalidOperationException("Storage não configurado.");
        var usarSql = await limite.RegistrarAsync(cmd.UsuarioId, cmd.TamanhoBytes, ct);
        var o = opcoes.Value;
        var agora = clock.UtcNow;

        var midia = new ArquivoMidia(
            Guid.CreateVersion7(), cmd.NomeOriginal, Normalizar(cmd.Titulo), Normalizar(cmd.Artista), cmd.TipoMidia,
            cmd.MimeTypeDeclarado, cmd.TamanhoBytes, cmd.UsuarioId, agora, TimeSpan.FromHours(o.Upload.LimpezaAguardandoHoras));

        var trava = Travas.UploadDeMidiaDoUsuario(LimiteDeUploadDeMidia.ChaveOpaca(hasher, cmd.UsuarioId));
        try
        {
            await transacao.ExecutarAsync([trava], EsperaDaTrava, async ct2 =>
            {
                // AguardandoUpload vencido não conta: a limpeza (F09) ainda não passou, mas o upload já não pode ser concluído.
                var abertos = await db.ArquivosMidia.CountAsync(
                    m => m.EnviadoPorUsuarioId == cmd.UsuarioId && Abertos.Contains(m.StatusSanitizacao)
                        && (m.StatusSanitizacao != StatusSanitizacao.AguardandoUpload || m.UploadExpiraEmUtc > agora), ct2);
                if (abertos >= o.MaximoAbertos) throw new LimiteExcedidoException(await EsperaDoLimiteDeAbertosAsync(cmd.UsuarioId, agora, ct2));

                if (usarSql)
                {
                    var corte = agora - LimiteDeUploadDeMidia.Janela;
                    var datas = await db.ArquivosMidia.Where(m => m.EnviadoPorUsuarioId == cmd.UsuarioId && m.DataUploadUtc > corte)
                        .OrderBy(m => m.DataUploadUtc).Select(m => m.DataUploadUtc).ToListAsync(ct2);
                    if (datas.Count >= o.UploadsPorHora)
                        throw new LimiteExcedidoException(TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, (datas[datas.Count - o.UploadsPorHora] + LimiteDeUploadDeMidia.Janela - agora).Ticks)));

                    // Orçamento de bytes: soma as linhas da hora, INCLUSIVE as canceladas (o cancelamento não devolve). O Retry-After é quando as mais antigas saem da janela o bastante para caber.
                    var linhas = await db.ArquivosMidia.Where(m => m.EnviadoPorUsuarioId == cmd.UsuarioId && m.DataUploadUtc > corte)
                        .OrderBy(m => m.DataUploadUtc).Select(m => new { m.DataUploadUtc, m.TamanhoBytes }).ToListAsync(ct2);
                    var soma = linhas.Sum(l => l.TamanhoBytes) + cmd.TamanhoBytes;
                    if (soma > o.BytesDeclaradosPorHora)
                    {
                        var i = 0;
                        while (i < linhas.Count && soma > o.BytesDeclaradosPorHora) soma -= linhas[i++].TamanhoBytes;
                        throw new LimiteExcedidoException(i == 0
                            ? LimiteDeUploadDeMidia.Janela
                            : TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, (linhas[i - 1].DataUploadUtc + LimiteDeUploadDeMidia.Janela - agora).Ticks)));
                    }
                }

                db.ArquivosMidia.Add(midia);
                await db.SaveChangesAsync(ct2);
                return true;
            }, ct);
        }
        catch (TravaOcupadaException)
        {
            if (!usarSql) await limite.DesfazerAsync(cmd.UsuarioId, cmd.TamanhoBytes, ct);
            throw new LimiteExcedidoException(TimeSpan.FromSeconds(1));
        }
        catch (LimiteExcedidoException)
        {
            if (!usarSql) await limite.DesfazerAsync(cmd.UsuarioId, cmd.TamanhoBytes, ct); // 5 abertos: nenhum envio nasceu
            throw;
        }

        try
        {
            var policy = await destino.CriarPostPolicyAsync(Buckets.Quarentena, midia.ChaveStorage, cmd.MimeTypeDeclarado, cmd.TamanhoBytes, ValidadeDaPolicy, ct);
            return new UploadIniciadoDto(midia.Id, new UploadDto(policy.Url, policy.Campos, policy.ExpiraEmUtc));
        }
        catch
        {
            // Sem policy o registro não serve a ninguém e ocuparia um dos 5 abertos até vencer: descarta (melhor esforço; a limpeza do F09 é a rede), e os contadores voltam.
            try
            {
                db.ArquivosMidia.Remove(midia);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // a exceção que importa é a da assinatura
            }

            if (!usarSql) await limite.DesfazerAsync(cmd.UsuarioId, cmd.TamanhoBytes, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Até o vencimento do <c>AguardandoUpload</c> mais antigo do usuário (mínimo 60 s); sem nenhum aberto, 60 s (02 §6).</summary>
    private async Task<TimeSpan> EsperaDoLimiteDeAbertosAsync(Guid usuario, DateTime agora, CancellationToken ct)
    {
        var vencimento = await db.ArquivosMidia
            .Where(m => m.EnviadoPorUsuarioId == usuario && m.StatusSanitizacao == StatusSanitizacao.AguardandoUpload && m.UploadExpiraEmUtc > agora)
            .MinAsync(m => (DateTime?)m.UploadExpiraEmUtc, ct);
        return vencimento is { } v && v - agora > EsperaComAbertosNoLimite ? v - agora : EsperaComAbertosNoLimite;
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
        // Cancelada, vencida ou já concluída: 409 (02 §6), sem nem olhar o storage. A corrida com o cancelamento cai no RowVersion, abaixo.
        if (midia.StatusSanitizacao != StatusSanitizacao.AguardandoUpload) throw new TransicaoInvalidaException("O envio não está mais aguardando o arquivo.");

        var info = await destino.HeadAsync(midia.Bucket, midia.ChaveStorage, ct)
            ?? throw Invalido("O arquivo ainda não chegou ao storage. Envie o arquivo antes de concluir.");
        if (info.TamanhoBytes != midia.TamanhoBytes) throw Invalido("O tamanho do arquivo enviado é diferente do declarado.");
        var etag = info.ETag?.Trim('"');
        if (string.IsNullOrEmpty(etag)) throw Invalido("O storage não devolveu o ETag do arquivo.");

        midia.ConcluirUpload(info.TamanhoBytes, etag, clock.UtcNow);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // O dono cancelou (ou o Worker mexeu) entre a leitura e o UPDATE: exatamente um vence, o outro recebe 409.
            throw new TransicaoInvalidaException("O envio foi cancelado ou mudou de status enquanto era concluído.");
        }
    }

    private static ValidationException Invalido(string mensagem) => new([new FluentValidation.Results.ValidationFailure("midiaId", mensagem)]);
}
