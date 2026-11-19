namespace WebRadio.Application.Abstracoes;

/// <summary>
/// Captura da fila de sanitização (E4-F05, Fluxo C passo 3): pega UMA mídia <c>Pendente</c> da mais antiga para a mais nova e a deixa <c>EmAnalise</c>, com o lease e a tentativa
/// +1. No SQL Server é a CTE <c>SELECT TOP (1) ... WITH (UPDLOCK, READPAST, ROWLOCK) ORDER BY DataUploadUtc</c> + <c>UPDATE</c> com <c>OUTPUT</c>: dois Workers nunca pegam o mesmo item.
/// </summary>
public interface ICapturaDeMidia
{
    /// <summary>O id da mídia capturada, ou <c>null</c> se a fila está vazia.</summary>
    Task<Guid?> CapturarProximaAsync(CancellationToken ct = default);
}

/// <summary>
/// "Worker degradado" (E4-D7, E4-D11, E4-D13): o healthcheck do contêiner é o heartbeat, então o estado vive num log <c>Error</c> <c>WORKER_DEGRADADO motivo=&lt;código&gt;</c> emitido A CADA
/// CICLO enquanto a condição durar (quem lê o log depois de um rotate ainda acha o marcador), mais um arquivo-marcador para o operador e o smoke; ao sair da condição, um
/// <c>Information</c> <c>WORKER_RECUPERADO motivo=&lt;código&gt;</c>. O <c>motivo</c> é sempre um de <see cref="MotivosDeDegradacao"/>.
/// </summary>
public interface IMarcadorDeDegradacao
{
    void Degradado(string motivo);

    void Recuperado(string motivo);
}
