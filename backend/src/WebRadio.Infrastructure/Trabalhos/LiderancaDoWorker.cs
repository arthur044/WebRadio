using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebRadio.Infrastructure.Trabalhos;

public sealed class LiderancaOptions
{
    public const string Recurso = "webradio-worker-leader";

    /// <summary>De quanto em quanto tempo o não-líder tenta o lock e o líder confere a própria sessão.</summary>
    public TimeSpan Intervalo { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Quem o ciclo do worker consulta antes de disparar um job (os testes do ciclo usam um dublê).</summary>
public interface ILideranca
{
    bool EhLider { get; }
}

/// <summary>
/// D4: eleição por <c>sp_getapplock</c> de SESSÃO (<c>webradio-worker-leader</c>, timeout 0). O lock vive enquanto a
/// conexão dedicada vive: matar o líder (ou a conexão cair) libera o lock, e outro worker o pega no próximo ciclo
/// (≤ 2 × Intervalo). A correção dos jobs NÃO depende do lock (UPDATE condicional + OUTPUT): ele só evita trabalho dobrado.
/// </summary>
public sealed class LiderancaDoWorker(string connectionString, LiderancaOptions opcoes, ILogger<LiderancaDoWorker> logger)
    : BackgroundService, ILideranca
{
    /// <summary>Application Name da conexão dedicada: identifica a sessão do líder em <c>sys.dm_exec_sessions</c>.</summary>
    public const string NomeDaAplicacao = "webradio-worker-leader";

    private SqlConnection? _conexao;
    private volatile bool _lider;

    public bool EhLider => _lider;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ct = stoppingToken;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_lider)
                    await ConferirAsync(ct);
                else
                    await TentarAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e) when (e is SqlException or InvalidOperationException or TimeoutException)
            {
                if (_lider) logger.LogWarning(e, "Liderança perdida (conexão com o SQL caiu); tentando de novo.");
                await PerderAsync();
            }

            try { await Task.Delay(opcoes.Intervalo, ct); }
            catch (OperationCanceledException) { break; }
        }

        await PerderAsync();
    }

    private async Task TentarAsync(CancellationToken ct)
    {
        // Pooling=false: com pool, o Dispose devolveria a conexão AINDA com o applock de sessão (o reset só acontece
        // no próximo uso), e um worker parado continuaria líder até o pool expirar a conexão.
        _conexao ??= new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false, ApplicationName = NomeDaAplicacao }.ConnectionString);
        if (_conexao.State != System.Data.ConnectionState.Open) await _conexao.OpenAsync(ct);

        await using var cmd = _conexao.CreateCommand();
        cmd.CommandText = "sp_getapplock";
        cmd.CommandType = System.Data.CommandType.StoredProcedure;
        cmd.Parameters.AddWithValue("@Resource", LiderancaOptions.Recurso);
        cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
        cmd.Parameters.AddWithValue("@LockOwner", "Session");
        cmd.Parameters.AddWithValue("@LockTimeout", 0);
        var retorno = cmd.Parameters.Add("@ret", System.Data.SqlDbType.Int);
        retorno.Direction = System.Data.ParameterDirection.ReturnValue;
        await cmd.ExecuteNonQueryAsync(ct);

        if ((int)retorno.Value >= 0)
        {
            _lider = true;
            logger.LogInformation("Este worker virou o líder.");
        }
    }

    /// <summary>O líder confirma que a SESSÃO ainda tem o lock (a conexão pode ter caído e voltado sem ele).</summary>
    private async Task ConferirAsync(CancellationToken ct)
    {
        await using var cmd = _conexao!.CreateCommand();
        cmd.CommandText = "SELECT APPLOCK_MODE('public', @r, 'Session')";
        cmd.Parameters.AddWithValue("@r", LiderancaOptions.Recurso);
        var modo = (string?)await cmd.ExecuteScalarAsync(ct);
        if (modo != "Exclusive")
        {
            logger.LogWarning("A sessão não tem mais o lock de liderança.");
            await PerderAsync();
        }
    }

    private async Task PerderAsync()
    {
        _lider = false;
        if (_conexao is not null)
        {
            await _conexao.DisposeAsync(); // fechar a conexão libera o applock de sessão
            _conexao = null;
        }
    }
}
