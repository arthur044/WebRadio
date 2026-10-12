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

    private EventoOutbox()
    {
    }

    /// <summary>01 §2.8: depois de 10 tentativas o evento fica parado para análise (não entra mais na fila de envio).</summary>
    public const short MaximoDeTentativas = 10;

    private const int TamanhoMaximoDoErro = 1000;

    public void MarcarProcessado(DateTime agora) => ProcessadoEmUtc = agora;

    public void RegistrarFalha(string erro)
    {
        Tentativas++;
        UltimoErro = erro.Length > TamanhoMaximoDoErro ? erro[..TamanhoMaximoDoErro] : erro;
    }

    /// <summary>Erro que nenhuma nova tentativa conserta (payload fora do envelope): sai da fila agora, com o motivo.</summary>
    public void Parar(string erro)
    {
        RegistrarFalha(erro);
        Tentativas = MaximoDeTentativas;
    }

    public bool Parado => ProcessadoEmUtc is null && Tentativas >= MaximoDeTentativas;

    public EventoOutbox(string tipo, string payload)
    {
        Tipo = tipo;
        Payload = payload;
        CriadoEmUtc = DateTime.UtcNow;
    }
}
