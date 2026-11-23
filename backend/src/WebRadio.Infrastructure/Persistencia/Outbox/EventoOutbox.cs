namespace WebRadio.Infrastructure.Persistencia.Outbox;

/// <summary>
/// infra.EventoOutbox (01-dominio.md §2.8). Mecanismo técnico, não conceito de domínio: vive em Infrastructure
/// e é gravado na mesma transação da mudança de estado (D22). O OutboxDispatcher (E1-F10) é quem consome.
/// </summary>
public sealed class EventoOutbox
{
    public long Id { get; private set; }

    public string Tipo { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public DateTime CriadoEmUtc { get; private set; }

    public DateTime? ProcessadoEmUtc { get; private set; }

    public short Tentativas { get; private set; }

    public string? UltimoErro { get; private set; }

    /// <summary>
    /// Backoff POR EVENTO (#25): depois de uma falha, o evento só volta à fila a partir deste instante; os outros eventos do lote seguem no ritmo normal. <c>null</c> = pronto para entregar.
    /// Parado (10 tentativas) ou entregue: <c>null</c> também.
    /// </summary>
    public DateTime? ProximaTentativaEmUtc { get; private set; }

    private EventoOutbox()
    {
    }

    /// <summary>01 §2.8: depois de 10 tentativas o evento fica parado para análise (não entra mais na fila de envio).</summary>
    public const short MaximoDeTentativas = 10;

    private const int TamanhoMaximoDoErro = 1000;

    /// <summary>Espera da 1ª falha; dobra a cada falha até <see cref="EsperaMaximaEntreTentativas"/> (5 s, 10 s, 20 s, ... 5 min): as 10 tentativas se espalham por ~25 min, e não por segundos.</summary>
    public static readonly TimeSpan EsperaInicialEntreTentativas = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan EsperaMaximaEntreTentativas = TimeSpan.FromMinutes(5);

    /// <summary>Quanto esperar depois da falha de número <paramref name="falhas"/> (1 = a primeira).</summary>
    public static TimeSpan EsperaDepoisDaFalha(int falhas)
    {
        var dobrada = EsperaInicialEntreTentativas.TotalSeconds * Math.Pow(2, Math.Max(0, falhas - 1));
        return TimeSpan.FromSeconds(Math.Min(dobrada, EsperaMaximaEntreTentativas.TotalSeconds));
    }

    public void MarcarProcessado(DateTime agora)
    {
        ProcessadoEmUtc = agora;
        ProximaTentativaEmUtc = null;
    }

    public void RegistrarFalha(string erro, DateTime agora)
    {
        Tentativas++;
        UltimoErro = erro.Length > TamanhoMaximoDoErro ? erro[..TamanhoMaximoDoErro] : erro;
        ProximaTentativaEmUtc = Tentativas >= MaximoDeTentativas ? null : agora + EsperaDepoisDaFalha(Tentativas);
    }

    /// <summary>Erro que nenhuma nova tentativa conserta (payload fora do envelope): sai da fila agora, com o motivo.</summary>
    public void Parar(string erro, DateTime agora)
    {
        RegistrarFalha(erro, agora);
        Tentativas = MaximoDeTentativas;
        ProximaTentativaEmUtc = null;
    }

    public bool Parado => ProcessadoEmUtc is null && Tentativas >= MaximoDeTentativas;

    /// <summary>O instante vem de quem enfileira (<c>IClock</c>): o outbox não lê o relógio do sistema por conta própria (#25).</summary>
    public EventoOutbox(string tipo, string payload, DateTime criadoEmUtc)
    {
        Tipo = tipo;
        Payload = payload;
        CriadoEmUtc = criadoEmUtc;
    }
}
