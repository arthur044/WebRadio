namespace WebRadio.Infrastructure.DependencyInjection;

/// <summary>
/// <c>Application Name</c> de cada serviço na connection string (aparece em <c>sys.dm_exec_sessions.program_name</c>).
/// Os valores seguem a tabela da §3 do <c>docs/conexao-sql-server.md</c>; o migrator (<c>WebRadio.Migrator</c>) e o
/// db-init (sqlcmd, que fixa <c>SQLCMD</c>) montam a própria conexão nos entrypoints. A conexão dedicada da liderança do
/// Worker é <see cref="Trabalhos.LiderancaDoWorker.NomeDaAplicacao"/>.
/// </summary>
public static class NomesDeAplicacao
{
    public const string Api = "WebRadio.Api";
    public const string Worker = "WebRadio.Worker";
}
