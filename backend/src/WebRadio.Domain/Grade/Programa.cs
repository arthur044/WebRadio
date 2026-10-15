using WebRadio.Domain.Common;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;

namespace WebRadio.Domain.Grade;

/// <summary>
/// schema grade. A invariante de não sobreposição (01-dominio.md §2.3) é checada na Application com
/// sp_getapplock (D7) — o Domain não consulta outros Programas.
/// </summary>
public sealed class Programa : Entidade
{
    public static readonly TimeSpan DuracaoMinima = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DuracaoMaxima = TimeSpan.FromHours(12);
    public static readonly TimeSpan ToleranciaInicioNoPassado = TimeSpan.FromMinutes(1);

    public string Titulo { get; private set; } = null!;

    public string? Descricao { get; private set; }

    public Guid LocutorId { get; private set; }

    public DateTime InicioUtc { get; private set; }

    public DateTime FimUtc { get; private set; }

    public StatusPrograma Status { get; private set; }

    public DateTime? CanceladoEmUtc { get; private set; }

    public DateTime CriadoEmUtc { get; private set; }

    public DateTime AtualizadoEmUtc { get; private set; }

    private Programa()
    {
    }

    public Programa(Guid id, string titulo, string? descricao, Guid locutorId, DateTime inicioUtc, DateTime fimUtc, DateTime agora)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titulo);
        UtcGuard.Exigir(agora, nameof(agora));
        ValidarJanela(inicioUtc, fimUtc, agora);

        Titulo = titulo;
        Descricao = descricao;
        LocutorId = locutorId;
        InicioUtc = inicioUtc;
        FimUtc = fimUtc;
        Status = StatusPrograma.Agendado;
        CriadoEmUtc = agora;
        AtualizadoEmUtc = agora;
    }

    /// <summary>Só Agendado pode ser editado. Reabre a checagem de sobreposição na Application.</summary>
    public void Editar(string titulo, string? descricao, Guid locutorId, DateTime inicioUtc, DateTime fimUtc, DateTime agora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titulo);
        UtcGuard.Exigir(agora, nameof(agora));
        GarantirStatus(StatusPrograma.Agendado);
        ValidarJanela(inicioUtc, fimUtc, agora);

        Titulo = titulo;
        Descricao = descricao;
        LocutorId = locutorId;
        InicioUtc = inicioUtc;
        FimUtc = fimUtc;
        AtualizadoEmUtc = agora;
    }

    /// <summary>Worker: Inicio ≤ agora &lt; Fim.</summary>
    public void IniciarAoVivo(DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));
        GarantirStatus(StatusPrograma.Agendado);

        if (!(InicioUtc <= agora && agora < FimUtc))
        {
            throw new TransicaoInvalidaException("Ainda não é hora de iniciar este programa (ou a janela já passou).");
        }

        Status = StatusPrograma.AoVivo;
        AtualizadoEmUtc = agora;
    }

    /// <summary>
    /// Worker: Fim ≤ agora, a partir de Agendado (janela perdida) ou AoVivo. Idempotente por natureza no
    /// worker (o UPDATE condicional só afeta linhas que ainda não viraram Finalizado).
    /// </summary>
    public void FinalizarPorFimDeJanela(DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));

        if (Status is not (StatusPrograma.Agendado or StatusPrograma.AoVivo))
        {
            throw new TransicaoInvalidaException($"Não é possível finalizar um programa {Status}.");
        }

        if (FimUtc > agora)
        {
            throw new TransicaoInvalidaException("O programa ainda não terminou.");
        }

        Status = StatusPrograma.Finalizado;
        AtualizadoEmUtc = agora;
    }

    /// <summary>
    /// Locutor dono ou Admin encerram o programa antes do fim previsto. Vale para <c>AoVivo</c> e também para <c>Agendado</c>
    /// cuja janela já começou (E2-F01): com o Worker parado o programa nunca passou para AoVivo, e o locutor que está no ar
    /// precisa conseguir encerrar mesmo assim. <c>FimUtc = máx(agora, InicioUtc + 1 s)</c>, truncado ao segundo (a coluna é
    /// <c>datetime2(0)</c>, que ARREDONDA): com <c>FimUtc = InicioUtc</c> o <c>CK_Programa_Intervalo</c> falharia no primeiro
    /// segundo. Nunca estende a janela: depois do fim previsto, <c>FimUtc</c> não muda.
    /// </summary>
    public void Encerrar(DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));

        if (Status is not (StatusPrograma.AoVivo or StatusPrograma.Agendado))
        {
            throw new TransicaoInvalidaException($"Não é possível encerrar um programa {Status}.");
        }

        if (agora < InicioUtc)
        {
            throw new TransicaoInvalidaException("Não é possível encerrar um programa antes do próprio início.");
        }

        var noSegundo = new DateTime(agora.Ticks - (agora.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        var novoFim = noSegundo > InicioUtc.AddSeconds(1) ? noSegundo : InicioUtc.AddSeconds(1);

        FimUtc = novoFim < FimUtc ? novoFim : FimUtc;
        Status = StatusPrograma.Finalizado;
        AtualizadoEmUtc = agora;
    }

    public void Cancelar(DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));
        GarantirStatus(StatusPrograma.Agendado);

        Status = StatusPrograma.Cancelado;
        CanceladoEmUtc = agora;
        AtualizadoEmUtc = agora;
    }

    private void GarantirStatus(StatusPrograma esperado)
    {
        if (Status != esperado)
        {
            throw new TransicaoInvalidaException($"Esperava o status {esperado}, mas o programa está {Status}.");
        }
    }

    private static void ValidarJanela(DateTime inicioUtc, DateTime fimUtc, DateTime agora)
    {
        UtcGuard.Exigir(inicioUtc, nameof(inicioUtc));
        UtcGuard.Exigir(fimUtc, nameof(fimUtc));

        if (fimUtc <= inicioUtc)
        {
            throw new ArgumentException("FimUtc precisa ser maior que InicioUtc.", nameof(fimUtc));
        }

        var duracao = fimUtc - inicioUtc;
        if (duracao < DuracaoMinima || duracao > DuracaoMaxima)
        {
            throw new ArgumentException(
                $"A duração precisa estar entre {DuracaoMinima} e {DuracaoMaxima} (recebido: {duracao}).",
                nameof(fimUtc));
        }

        if (inicioUtc < agora - ToleranciaInicioNoPassado)
        {
            throw new ArgumentException(
                "Não é possível criar/editar um programa cujo início já passou há mais de 1 minuto.",
                nameof(inicioUtc));
        }
    }
}
