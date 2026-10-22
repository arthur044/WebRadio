using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Application.Features.Harbor;

/// <summary>O que o <c>harbor/auth</c> deixa na fila: quais alertas a falha disparou e o identificador (só para achar o dono; nunca sai no evento).</summary>
public sealed record AlertaPendente(AlertasDaFalha Alertas, Guid Identificador)
{
    public override string ToString() => $"{nameof(AlertaPendente)} {Alertas}";
}

/// <summary>
/// E2-F12: fila em memória entre o <c>harbor/auth</c> e o serviço em segundo plano. O <c>auth</c> só enfileira (nenhuma consulta, nenhuma
/// gravação) e responde o 401 no tempo de sempre. Fila cheia (<see cref="CapacidadePadrao"/> itens) descarta o alerta e loga
/// <c>HARBOR_ALERTA_FALHOU</c>: perder um alerta é aceito (um reinício da API também perde a fila), atrasar ou derrubar o <c>auth</c> não.
/// </summary>
public sealed class FilaDeAlertasDoHarbor
{
    public const int CapacidadePadrao = 100;

    /// <summary>Teto de consultas do dono por minuto (E2-F12): o consumidor se espaça por isto; o que não couber na fila é descartado.</summary>
    public const int MaximoDeConsultasPorMinuto = 30;

    public static readonly TimeSpan EspacoEntreConsultas = TimeSpan.FromMinutes(1.0 / MaximoDeConsultasPorMinuto);

    private readonly Channel<AlertaPendente> _canal;
    private readonly ILogger<FilaDeAlertasDoHarbor> _logger;

    public FilaDeAlertasDoHarbor(ILogger<FilaDeAlertasDoHarbor> logger, int capacidade = CapacidadePadrao)
    {
        _logger = logger;
        _canal = Channel.CreateBounded<AlertaPendente>(new BoundedChannelOptions(capacidade)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite devolve false quando cheia; quem descarta somos nós, com log
            SingleWriter = false,
            SingleReader = false,
        });
    }

    public void Enfileirar(AlertaPendente alerta)
    {
        if (alerta.Alertas == AlertasDaFalha.Nenhum) return;
        if (!_canal.Writer.TryWrite(alerta))
            _logger.LogWarning("HARBOR_ALERTA_FALHOU fila cheia: alerta {Alertas} descartado", alerta.Alertas);
    }

    public bool TentarLer(out AlertaPendente? alerta) => _canal.Reader.TryRead(out alerta);

    public ValueTask<AlertaPendente> LerAsync(CancellationToken ct) => _canal.Reader.ReadAsync(ct);
}

/// <summary>
/// E2-F12 (risco aceito 3). Roda no serviço em segundo plano, num escopo (e num <c>DbContext</c>) próprio: ou seja, numa transação
/// própria, depois de o 401 ter saído. Procura o dono do identificador entre as credenciais ativas e as revogadas ainda não apagadas
/// pela F09 e grava o <c>AlertaHarborFalhas</c> no outbox: tipo <c>credencial</c> (com <c>credencialAtiva</c>) para <c>admins</c> e o
/// dono; tipo <c>global</c> só para <c>admins</c>. Identificador que não é de credencial nenhuma não gera o tipo <c>credencial</c>.
/// O payload não leva segredo, identificador da credencial nem IP. Qualquer falha loga <c>HARBOR_ALERTA_FALHOU</c> e o alerta se perde.
/// </summary>
public sealed class AlertaDoHarbor(IConsultasDoHarbor consultas, IOutbox outbox, IRadioDbContext db, ILogger<AlertaDoHarbor> logger)
{
    public const string TipoCredencial = "credencial";
    public const string TipoGlobal = "global";

    /// <summary>true quando o alerta faz a consulta do dono (a que o <see cref="FilaDeAlertasDoHarbor.MaximoDeConsultasPorMinuto"/> limita).</summary>
    public static bool ConsultaODono(AlertaPendente pendente) => pendente.Alertas.HasFlag(AlertasDaFalha.Identificador);

    public async Task ProcessarAsync(AlertaPendente pendente, CancellationToken ct)
    {
        try
        {
            var janelaMinutos = (int)ThrottleDoHarbor.Janela.TotalMinutes;
            var algum = false;

            if (pendente.Alertas.HasFlag(AlertasDaFalha.Identificador)
                && await consultas.DonoDaCredencialAsync(pendente.Identificador, ct) is { } dono)
            {
                outbox.EnfileirarParaGrupos([GruposDoHub.Admins, GruposDoHub.Usuario(dono.UsuarioId)], EventosDoOutbox.AlertaHarborFalhas,
                    new
                    {
                        tipo = TipoCredencial,
                        falhas = ThrottleDoHarbor.AlertaPorIdentificador,
                        janelaMinutos,
                        locutorId = dono.UsuarioId,
                        locutorNome = dono.Nome,
                        credencialAtiva = dono.Ativa,
                    });
                algum = true;
            }

            if (pendente.Alertas.HasFlag(AlertasDaFalha.Global))
            {
                outbox.EnfileirarParaGrupos([GruposDoHub.Admins], EventosDoOutbox.AlertaHarborFalhas,
                    new { tipo = TipoGlobal, falhas = ThrottleDoHarbor.AlertaGlobal, janelaMinutos });
                algum = true;
            }

            if (algum) await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "HARBOR_ALERTA_FALHOU: o AlertaHarborFalhas não foi gravado no outbox e se perdeu; o harbor/auth não foi afetado");
        }
    }
}
