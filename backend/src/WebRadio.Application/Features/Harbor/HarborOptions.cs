namespace WebRadio.Application.Features.Harbor;

/// <summary>
/// <c>Harbor__AtrasDoProxy</c> (06 "Throttle do harbor/auth", último item). Atrás do <c>harbor-borda</c> (E2-F11) o Liquidsoap só vê
/// o IP do proxy: o par (identificador, IP) viraria bloqueio por identificador, que é proibido (lockout do locutor). Com true a
/// API não bloqueia por IP nem por par, só registra e alerta; o limite por cliente fica no nginx. Padrão false (acesso direto).
/// </summary>
public sealed class HarborOptions
{
    public const string Secao = "Harbor";

    public bool AtrasDoProxy { get; set; }
}
