using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Midia;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Trabalhos;

/// <summary>Seção <c>Manutencao</c> da limpeza de mídia (08 E4-D11): <c>Manutencao:Midia:LimiteExclusao</c> e <c>Manutencao:Quarentena:IntervaloOrfaosMinutos</c>.</summary>
public sealed class LimpezaDeMidiaOptions
{
    /// <summary>Trava contra apagão: a rodada de <c>midia/</c> aborta se apagaria mais que isto (objetos) ...</summary>
    public int LimiteExclusaoDeObjetos { get; set; } = 20;

    /// <summary>... ou mais que esta fração do que listou (10%).</summary>
    public double LimiteExclusaoFracao { get; set; } = 0.10;

    /// <summary>Primeiro ano com objeto: a listagem é por prefixo de ano (<c>20??/</c>, a política do <c>svc-worker</c> só deixa listar assim).</summary>
    public int PrimeiroAno { get; set; } = 2026;

    public TimeSpan IdadeMinimaDoOrfaoDaQuarentena { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan IdadeMinimaDoOrfaoDeMidia { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan RetencaoDaQuarentena { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan RetencaoDoCancelado { get; set; } = TimeSpan.FromHours(24);
}

public sealed record ResultadoDaLimpezaDeMidia(int Cancelados, int AguardandoVencidos, int OrfaosDeMidia, bool TravaDeApagao);

public interface ILimpezaDeMidia
{
    /// <summary>Rodada diária (dentro da <c>Manutencao</c>): cancelados, <c>AguardandoUpload</c> vencido e órfãos de <c>midia/</c> (com a trava contra apagão).</summary>
    Task<ResultadoDaLimpezaDeMidia> ExecutarDiariaAsync(CancellationToken ct);

    /// <summary>A cada 30 min: órfãos do prefixo <c>quarentena/</c> (inclui o <c>Quarentena</c> com mais de 30 dias). Devolve quantos objetos apagou.</summary>
    Task<int> VarrerOrfasDaQuarentenaAsync(CancellationToken ct);
}

/// <summary>
/// E4-F09 (08 E4-D11, E4-D14). Tudo que apaga OBJETO antes confere o BANCO imediatamente; tudo que apaga REGISTRO é condicionado ao status (<c>DELETE ... WHERE StatusSanitizacao = ...</c>) e o objeto só
/// sai se a linha saiu: um <c>concluir</c> que vence a corrida deixa o registro (e o objeto) em paz. Falha de permissão ou de storage vira aviso, nunca exceção que derruba a manutenção.
/// </summary>
public sealed partial class LimpezaDeMidia(RadioDbContext db, IObjectStorage storage, IClock relogio, IMarcadorDeDegradacao marcador, LimpezaDeMidiaOptions opcoes, ILogger<LimpezaDeMidia> logger) : ILimpezaDeMidia
{
    private const int LoteDeIds = 1000;

    // {yyyy}/{MM}/{guid} na quarentena; {yyyy}/{MM}/{guid}.mp3 em midia (08 E4-D11). Chave fora do formato nunca é tocada.
    [GeneratedRegex(@"^\d{4}/\d{2}/(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$")]
    private static partial Regex ChaveDaQuarentena();

    [GeneratedRegex(@"^\d{4}/\d{2}/(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.mp3$")]
    private static partial Regex ChaveDeMidia();

    public async Task<ResultadoDaLimpezaDeMidia> ExecutarDiariaAsync(CancellationToken ct)
    {
        var agora = relogio.UtcNow;

        // Cancelado pelo dono há mais de 24 h: só o registro (o objeto, se subiu, já saiu como órfão na varredura de 30 min).
        var corteCancelado = agora - opcoes.RetencaoDoCancelado;
        var cancelados = await db.ArquivosMidia
            .Where(m => m.StatusSanitizacao == StatusSanitizacao.Rejeitado && m.MotivoRejeicao == MotivosRejeicao.EnvioCancelado && m.SanitizadoEmUtc < corteCancelado)
            .ExecuteDeleteAsync(ct);

        var vencidos = await LimparAguardandoVencidosAsync(agora, ct);
        var (orfaos, trava) = await LimparOrfasDeMidiaAsync(ct);
        return new ResultadoDaLimpezaDeMidia(cancelados, vencidos, orfaos, trava);
    }

    /// <summary>
    /// <c>AguardandoUpload</c> vencido (<c>UploadExpiraEmUtc</c> &lt; agora): apaga o REGISTRO com <c>DELETE</c> condicionado ao status e só então o objeto. Se um <c>concluir</c> venceu a corrida, a linha já
    /// não é <c>AguardandoUpload</c>, o <c>DELETE</c> afeta 0 linhas e o objeto fica.
    /// </summary>
    private async Task<int> LimparAguardandoVencidosAsync(DateTime agora, CancellationToken ct)
    {
        var candidatos = await db.ArquivosMidia.AsNoTracking()
            .Where(m => m.StatusSanitizacao == StatusSanitizacao.AguardandoUpload && m.UploadExpiraEmUtc < agora)
            .Select(m => new { m.Id, m.ChaveStorage })
            .ToListAsync(ct);

        var apagados = 0;
        foreach (var c in candidatos)
        {
            var linhas = await db.ArquivosMidia
                .Where(m => m.Id == c.Id && m.StatusSanitizacao == StatusSanitizacao.AguardandoUpload && m.UploadExpiraEmUtc < agora)
                .ExecuteDeleteAsync(ct);
            if (linhas == 0) continue; // o concluir (ou o cancelamento) ganhou: nada a apagar
            apagados++;
            await TentarApagarObjetoAsync(Buckets.Quarentena, c.ChaveStorage, ct);
        }

        return apagados;
    }

    public async Task<int> VarrerOrfasDaQuarentenaAsync(CancellationToken ct)
    {
        var agora = relogio.UtcNow;
        var corteDaIdade = agora - opcoes.IdadeMinimaDoOrfaoDaQuarentena;
        var corteDaRetencao = agora - opcoes.RetencaoDaQuarentena;

        var objetos = await ListarPorAnoAsync(Buckets.Quarentena, ct);
        if (objetos is null) return 0;

        var candidatos = new Dictionary<Guid, string>();
        foreach (var o in objetos)
        {
            if (o.UltimaModificacaoUtc >= corteDaIdade) continue;
            if (ChaveDaQuarentena().Match(o.Chave) is not { Success: true } m) continue;
            candidatos[Guid.Parse(m.Groups["id"].Value)] = o.Chave;
        }

        // Quem AINDA deve ter objeto: AguardandoUpload, Pendente, EmAnalise, ou Quarentena dentro dos 30 dias. Reconferido por id, em lotes (limite de parâmetros do SQL).
        var validos = new HashSet<Guid>();
        foreach (var lote in candidatos.Keys.Chunk(LoteDeIds))
        {
            var ids = lote;
            var achados = await db.ArquivosMidia.AsNoTracking()
                .Where(m => ids.Contains(m.Id) && (m.StatusSanitizacao == StatusSanitizacao.AguardandoUpload || m.StatusSanitizacao == StatusSanitizacao.Pendente
                    || m.StatusSanitizacao == StatusSanitizacao.EmAnalise || (m.StatusSanitizacao == StatusSanitizacao.Quarentena && m.SanitizadoEmUtc >= corteDaRetencao)))
                .Select(m => m.Id).ToListAsync(ct);
            validos.UnionWith(achados);
        }

        var apagados = 0;
        foreach (var (id, chave) in candidatos)
        {
            if (validos.Contains(id)) continue;
            if (await TentarApagarObjetoAsync(Buckets.Quarentena, chave, ct)) apagados++;
        }

        if (apagados > 0) logger.LogInformation("WORKER_LIMPEZA_QUARENTENA: {Apagados} objeto(s) órfão(s) apagado(s) da quarentena.", apagados);
        return apagados;
    }

    /// <summary>
    /// Órfão em <c>midia/</c> = objeto cujo registro não existe ou não está <c>Aprovado</c>, com mais de 24 h (é como a exclusão de mídia apaga o arquivo, E4-D14). TRAVA CONTRA APAGÃO: se a
    /// consulta ao banco falhar, ou se a rodada apagaria mais de <see cref="LimpezaDeMidiaOptions.LimiteExclusaoDeObjetos"/> objetos ou mais de 10% do que listou, aborta SEM apagar nada e emite
    /// <c>WORKER_DEGRADADO motivo=trava_apagao</c>: um Worker apontando para um banco vazio ou restaurado veria a biblioteca inteira como órfã.
    /// </summary>
    private async Task<(int Apagados, bool Trava)> LimparOrfasDeMidiaAsync(CancellationToken ct)
    {
        var objetos = await ListarPorAnoAsync(Buckets.Midia, ct);
        if (objetos is null) return (0, false); // permissão ou storage: já virou aviso; não é apagão

        var corte = relogio.UtcNow - opcoes.IdadeMinimaDoOrfaoDeMidia;
        var listados = new Dictionary<Guid, (string Chave, DateTime Quando)>();
        foreach (var o in objetos)
        {
            if (ChaveDeMidia().Match(o.Chave) is not { Success: true } m) continue;
            listados[Guid.Parse(m.Groups["id"].Value)] = (o.Chave, o.UltimaModificacaoUtc);
        }

        HashSet<Guid> aprovados;
        try
        {
            aprovados = [];
            foreach (var lote in listados.Keys.Chunk(LoteDeIds))
            {
                var ids = lote;
                aprovados.UnionWith(await db.ArquivosMidia.AsNoTracking().Where(m => ids.Contains(m.Id) && m.StatusSanitizacao == StatusSanitizacao.Aprovado).Select(m => m.Id).ToListAsync(ct));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            marcador.Degradado(MotivosDeDegradacao.TravaApagao);
            logger.LogError("WORKER_LIMPEZA_MIDIA_ABORTADA ({Tipo}): a consulta ao banco falhou; nada foi apagado em midia/.", e.GetType().Name);
            return (0, true);
        }

        var orfaos = listados.Where(x => !aprovados.Contains(x.Key) && x.Value.Quando < corte).ToList();
        // Sempre cabe ao menos 1 órfão por rodada: com poucos objetos, 10% arredondaria para 0 e a limpeza nunca andaria.
        var teto = Math.Min(opcoes.LimiteExclusaoDeObjetos, Math.Max(1, (int)Math.Floor(listados.Count * opcoes.LimiteExclusaoFracao)));
        if (orfaos.Count > teto)
        {
            marcador.Degradado(MotivosDeDegradacao.TravaApagao);
            logger.LogError("WORKER_LIMPEZA_MIDIA_ABORTADA: {Orfaos} órfão(s) em {Listados} objeto(s) listado(s) passam do teto de {Teto}; nada foi apagado em midia/ (banco vazio ou restaurado?).",
                orfaos.Count, listados.Count, teto);
            return (0, true);
        }

        marcador.Recuperado(MotivosDeDegradacao.TravaApagao);
        var apagados = 0;
        foreach (var (id, (chave, _)) in orfaos)
        {
            // Reconfere logo antes de apagar: uma aprovação nascida depois da leitura não perde o arquivo.
            if (await db.ArquivosMidia.AsNoTracking().AnyAsync(m => m.Id == id && m.StatusSanitizacao == StatusSanitizacao.Aprovado, ct)) continue;
            if (await TentarApagarObjetoAsync(Buckets.Midia, chave, ct)) apagados++;
        }

        if (apagados > 0) logger.LogInformation("WORKER_LIMPEZA_MIDIA: {Apagados} objeto(s) órfão(s) apagado(s) de midia/.", apagados);
        return (apagados, false);
    }

    /// <summary>Lista pelos prefixos de ano (<c>2026/</c>... até o ano de hoje): a política do <c>svc-worker</c> só deixa listar <c>20??/</c>. <c>null</c> = falha de permissão ou de storage (vira aviso).</summary>
    private async Task<List<ObjetoListado>?> ListarPorAnoAsync(string bucket, CancellationToken ct)
    {
        var objetos = new List<ObjetoListado>();
        try
        {
            for (var ano = opcoes.PrimeiroAno; ano <= relogio.UtcNow.Year; ano++)
                objetos.AddRange(await storage.ListarAsync(bucket, $"{ano}/", ct));
            return objetos;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("WORKER_LIMPEZA_MIDIA_LISTAGEM ({Tipo}): não consegui listar {Bucket}; tenta de novo na próxima rodada.", e.GetType().Name, bucket);
            return null;
        }
    }

    private async Task<bool> TentarApagarObjetoAsync(string bucket, string chave, CancellationToken ct)
    {
        try
        {
            await storage.DeleteAsync(bucket, chave, ct);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("WORKER_LIMPEZA_MIDIA_NAO_APAGOU ({Tipo}): o objeto sobra para a próxima rodada.", e.GetType().Name);
            return false;
        }
    }
}
