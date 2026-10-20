using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Transmissao;

namespace WebRadio.Application.Features.Harbor;

/// <summary>
/// O ao vivo agora (<c>aoVivo</c> do <c>GET /stream/info</c>). <c>Identificador</c> é de quem conectou (uma desconexão de outro não
/// derruba o estado); <c>DesdeUtc</c> ordena os eventos; <c>FimUtc</c> é o fim previsto do programa.
/// </summary>
public sealed record EstadoDoAoVivo(bool NoAr, Guid? ProgramaId, string? LocutorNome, Guid? Identificador, DateTime DesdeUtc, DateTime? FimUtc)
{
    /// <summary>Teto da falha aberta do Liquidsoap (E2-F07): depois disso ninguém mais está no ar, mesmo sem o evento Desconectado.</summary>
    public static readonly TimeSpan MargemDoFim = TimeSpan.FromMinutes(1);

    public static EstadoDoAoVivo ForaDoAr(DateTime desdeUtc) => new(false, null, null, null, desdeUtc, null);

    /// <summary>"No ar" com o fim previsto + 1 min já passado vira "fora do ar": o evento de desconexão pode ter se perdido.</summary>
    public EstadoDoAoVivo Vigente(DateTime agora) => NoAr && FimUtc is { } fim && agora >= fim + MargemDoFim ? ForaDoAr(DesdeUtc) : this;
}

/// <summary>O que <see cref="IEstadoDoAoVivoStore.ExpirarVencidoAsync"/> trocou: o estado vencido e o que o substituiu.</summary>
public sealed record ExpiracaoDoAoVivo(EstadoDoAoVivo Antes, EstadoDoAoVivo Depois);

/// <summary>Em memória e, quando há Redis, compartilhado entre as réplicas (como o "tocando agora").</summary>
public interface IEstadoDoAoVivoStore
{
    Task<EstadoDoAoVivo?> ObterAsync(CancellationToken ct);

    Task DefinirAsync(EstadoDoAoVivo estado, CancellationToken ct);

    /// <summary>
    /// Se o estado guardado é "no ar" e já passou do fim previsto + 1 min (<see cref="EstadoDoAoVivo.Vigente"/>), troca por "fora do
    /// ar" e devolve a troca; senão <c>null</c>. Com Redis a troca é condicional ao valor lido: de N réplicas, só uma a faz.
    /// </summary>
    Task<ExpiracaoDoAoVivo?> ExpirarVencidoAsync(CancellationToken ct);
}

/// <summary>
/// Um <c>Desconectado</c> perdido (o Liquidsoap caiu, a API estava fora) deixa o estado "no ar" até <c>fim + 1 min</c>, quando o
/// <see cref="EstadoDoAoVivo.Vigente"/> passa a responder "fora do ar" ao <c>GET /stream/info</c>. Quem já está conectado ao hub
/// não leria isso: aqui o vencimento também vira <c>TransmissaoAoVivoAlterada</c>. Se não der para gravar o evento, o estado vencido
/// volta para o store e a próxima passada tenta de novo.
/// </summary>
public sealed class ReconciliadorDoAoVivo(IEstadoDoAoVivoStore estado, IOutbox outbox, IRadioDbContext db, ILogger<ReconciliadorDoAoVivo> logger)
{
    public async Task<bool> ExecutarAsync(CancellationToken ct)
    {
        if (await estado.ExpirarVencidoAsync(ct) is not { } troca) return false;

        try
        {
            outbox.EnfileirarParaTodos(EventosDoOutbox.TransmissaoAoVivoAlterada, new { noAr = false, programaId = (Guid?)null, locutorNome = (string?)null });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await estado.DefinirAsync(troca.Antes, CancellationToken.None);
            logger.LogError(ex, "HARBOR_AO_VIVO_VENCIDO não publicou: o estado voltou e a próxima passada tenta de novo");
            return false;
        }

        logger.LogWarning("HARBOR_AO_VIVO_VENCIDO programa {ProgramaId} fim previsto {FimUtc}: sem Desconectado, saiu do ar pelo fim + 1 min",
            troca.Antes.ProgramaId, troca.Antes.FimUtc);
        return true;
    }
}

/// <summary>06 "Endpoints internos": <c>POST /internal/harbor/eventos</c>, do <c>on_connect</c>/<c>on_disconnect</c> do harbor.</summary>
public sealed record RegistrarEventoDoHarborCommand(string? Tipo, string? Identificador, DateTime? OcorridoEmUtc) : IRequest;

public sealed class RegistrarEventoDoHarborValidator : AbstractValidator<RegistrarEventoDoHarborCommand>
{
    public const string Conectado = "Conectado";
    public const string Desconectado = "Desconectado";

    public RegistrarEventoDoHarborValidator(IClock clock)
    {
        RuleFor(x => x.Tipo).Must(t => t is Conectado or Desconectado).WithMessage($"Tipo de evento desconhecido (aceitos: {Conectado}, {Desconectado}).");
        RuleFor(x => x.Identificador).Must(SegredoDeTransmissao.UsuarioValido).WithMessage("identificador inválido.");
        RuleFor(x => x.OcorridoEmUtc).NotNull().WithMessage("Informe ocorridoEmUtc.")
            .Must(quando => quando is null || quando.Value.ToUniversalTime() <= clock.UtcNow + Playout.RegistrarEventoDoPlayoutValidator.ToleranciaDeRelogio)
            .WithMessage("ocorridoEmUtc não pode estar no futuro.");
    }
}

/// <summary>
/// Muda o estado e publica <c>TransmissaoAoVivoAlterada</c> só quando o estado muda: evento repetido (o Liquidsoap reenvia) e evento
/// atrasado (mais velho que o estado atual) não publicam nada. O programa e o locutor vêm da janela no SQL, não do Liquidsoap.
/// </summary>
public sealed class RegistrarEventoDoHarborHandler(
    IConsultasDoHarbor consultas,
    IEstadoDoAoVivoStore estado,
    IOutbox outbox,
    IRadioDbContext db,
    ILogger<RegistrarEventoDoHarborHandler> logger) : IRequestHandler<RegistrarEventoDoHarborCommand>
{
    public async Task Handle(RegistrarEventoDoHarborCommand cmd, CancellationToken ct)
    {
        var identificador = Guid.ParseExact(cmd.Identificador!, "D");
        var quando = DateTime.SpecifyKind(cmd.OcorridoEmUtc!.Value.ToUniversalTime(), DateTimeKind.Utc);
        var atual = await estado.ObterAsync(ct);
        if (atual is not null && quando < atual.DesdeUtc) return;

        EstadoDoAoVivo novo;
        if (cmd.Tipo == RegistrarEventoDoHarborValidator.Conectado)
        {
            var janela = await consultas.JanelaAsync(identificador, ct);
            if (janela is null)
            {
                // O auth acabou de aceitar; a janela pode ter fechado no meio. A consulta periódica derruba em seguida.
                logger.LogWarning("HARBOR_EVENTO_IGNORADO Conectado fora da janela: identificador {Identificador}", identificador);
                return;
            }

            if (atual is { NoAr: true } && atual.Identificador == identificador && atual.ProgramaId == janela.ProgramaId) return;

            var locutor = await (from p in db.Programas.AsNoTracking()
                                 join u in db.Usuarios.AsNoTracking() on p.LocutorId equals u.Id
                                 where p.Id == janela.ProgramaId
                                 select u.Nome).FirstOrDefaultAsync(ct);
            novo = new EstadoDoAoVivo(true, janela.ProgramaId, locutor, identificador, quando, janela.FimUtc);
        }
        else
        {
            if (atual is not { NoAr: true } || atual.Identificador != identificador) return;
            novo = EstadoDoAoVivo.ForaDoAr(quando);
        }

        outbox.EnfileirarParaTodos(EventosDoOutbox.TransmissaoAoVivoAlterada, new { noAr = novo.NoAr, programaId = novo.ProgramaId, locutorNome = novo.LocutorNome });
        await db.SaveChangesAsync(ct);
        await estado.DefinirAsync(novo, ct);
        logger.LogInformation("HARBOR_AO_VIVO {Tipo} identificador {Identificador} programa {ProgramaId}", cmd.Tipo, identificador, novo.ProgramaId ?? atual?.ProgramaId);
    }
}
