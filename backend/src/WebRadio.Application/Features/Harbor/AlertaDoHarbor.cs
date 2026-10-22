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
/// E2-F12: filas em memória entre o <c>harbor/auth</c> e o serviço em segundo plano. O <c>auth</c> só enfileira (nenhuma consulta,
/// nenhuma gravação) e responde o 401 no tempo de sempre. Fila cheia descarta o alerta e loga <c>HARBOR_ALERTA_FALHOU</c>: perder um
/// alerta é aceito (um reinício da API também perde a fila), atrasar ou derrubar o <c>auth</c> não.
/// Duas filas: a de credenciais (<see cref="CapacidadePadrao"/> itens), que depende da consulta do dono e é espaçada pelo consumidor, e a
/// do global, que é só uma gravação no outbox, sai no máximo uma vez por janela e nunca espera atrás de consulta. Assim uma varredura
/// com identificadores aleatórios, que enche a fila de credenciais, não esconde o sinal mais amplo.
/// </summary>
public sealed class FilaDeAlertasDoHarbor
{
    public const int CapacidadePadrao = 100;

    /// <summary>O global sai uma vez por janela de 5 min; 10 vagas sobram.</summary>
    public const int CapacidadeDoGlobal = 10;

    /// <summary>Teto de consultas do dono por minuto (E2-F12): o consumidor se espaça por isto; o que não couber na fila é descartado.</summary>
    public const int MaximoDeConsultasPorMinuto = 30;

    public static readonly TimeSpan EspacoEntreConsultas = TimeSpan.FromMinutes(1.0 / MaximoDeConsultasPorMinuto);

    private readonly Channel<AlertaPendente> _credenciais;
    private readonly Channel<AlertaPendente> _globais;
    private readonly ILogger<FilaDeAlertasDoHarbor> _logger;

    public FilaDeAlertasDoHarbor(ILogger<FilaDeAlertasDoHarbor> logger, int capacidade = CapacidadePadrao)
    {
        _logger = logger;
        _credenciais = Nova(capacidade);
        _globais = Nova(CapacidadeDoGlobal);
    }

    // FullMode.Wait: TryWrite devolve false quando cheia; quem descarta somos nós, com log.
    private static Channel<AlertaPendente> Nova(int capacidade) =>
        Channel.CreateBounded<AlertaPendente>(new BoundedChannelOptions(capacidade) { FullMode = BoundedChannelFullMode.Wait });

    public void Enfileirar(AlertaPendente alerta)
    {
        if (alerta.Alertas.HasFlag(AlertasDaFalha.Identificador))
            Escrever(_credenciais, new AlertaPendente(AlertasDaFalha.Identificador, alerta.Identificador));
        if (alerta.Alertas.HasFlag(AlertasDaFalha.Global))
            Escrever(_globais, new AlertaPendente(AlertasDaFalha.Global, alerta.Identificador));
    }

    private void Escrever(Channel<AlertaPendente> fila, AlertaPendente alerta)
    {
        if (!fila.Writer.TryWrite(alerta))
            _logger.LogWarning("HARBOR_ALERTA_FALHOU fila cheia: alerta {Alertas} descartado", alerta.Alertas);
    }

    /// <summary>Tira o próximo sem esperar, o global primeiro (testes e drenagem manual).</summary>
    public bool TentarLer(out AlertaPendente? alerta) => _globais.Reader.TryRead(out alerta) || _credenciais.Reader.TryRead(out alerta);

    public ValueTask<AlertaPendente> LerCredencialAsync(CancellationToken ct) => _credenciais.Reader.ReadAsync(ct);

    public ValueTask<AlertaPendente> LerGlobalAsync(CancellationToken ct) => _globais.Reader.ReadAsync(ct);
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
