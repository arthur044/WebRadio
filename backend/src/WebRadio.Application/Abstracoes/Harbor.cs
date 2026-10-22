namespace WebRadio.Application.Abstracoes;

/// <summary>Salt e hash da credencial ATIVA de um identificador (o resto da condição é da <see cref="IConsultasDoHarbor.JanelaAsync"/>).</summary>
public sealed record CredencialDoHarbor(Guid Id, byte[] Salt, byte[] SegredoHash)
{
    public override string ToString() => $"{nameof(CredencialDoHarbor)} {Id}";
}

/// <summary>Dono de uma credencial, ativa ou revogada: destino e texto do <c>AlertaHarborFalhas</c> (E2-F12).</summary>
public sealed record DonoDaCredencial(Guid UsuarioId, string Nome, bool Ativa);

/// <summary>Resposta 200 de <c>harbor/auth</c> e <c>harbor/autorizacao</c>: o programa no ar e o fim previsto.</summary>
public sealed record JanelaDoHarbor(Guid ProgramaId, DateTime FimUtc);

/// <summary>
/// Consultas do ao vivo (06 "Autorização do ao vivo: pela janela"). A janela é calculada NO SQL, com <c>SYSUTCDATETIME()</c>: o
/// <c>Status = AoVivo</c> não entra na conta, então o locutor entra no segundo do início sem esperar o Worker e, com o Worker
/// parado, cai quando <c>FimUtc</c> passa.
/// </summary>
public interface IConsultasDoHarbor
{
    Task<CredencialDoHarbor?> CredencialAtivaAsync(Guid identificador, CancellationToken ct);

    /// <summary>
    /// Credencial ativa, dono ativo com papel Locutor ou Admin, e um programa DELE com <c>InicioUtc &lt;= agora &lt; FimUtc</c> e
    /// <c>Status NOT IN (Finalizado, Cancelado)</c>. null = fora da janela (o motivo nunca sai daqui).
    /// </summary>
    Task<JanelaDoHarbor?> JanelaAsync(Guid identificador, CancellationToken ct);

    /// <summary>
    /// Dono da credencial do identificador, ativa ou revogada ainda não apagada (E2-F09); null se nenhuma credencial tem esse
    /// identificador. Só o alerta do harbor chama (E2-F12), nunca o caminho comum do <c>auth</c>.
    /// </summary>
    Task<DonoDaCredencial?> DonoDaCredencialAsync(Guid identificador, CancellationToken ct);

    /// <summary><c>UltimoUsoEmUtc = SYSUTCDATETIME()</c> no sucesso do <c>auth</c>.</summary>
    Task RegistrarUsoAsync(Guid credencialId, CancellationToken ct);
}
