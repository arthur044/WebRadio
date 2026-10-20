using System.Net;
using MediatR;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Transmissao;

namespace WebRadio.Application.Features.Harbor;

/// <summary><c>POST /internal/harbor/auth</c>. null = 401 sem corpo: o motivo nunca sai na resposta.</summary>
public sealed record AutenticarNoHarborCommand(string? Usuario, string? Senha, string? EnderecoIp) : IRequest<JanelaDoHarbor?>
{
    public override string ToString() => nameof(AutenticarNoHarborCommand);
}

/// <summary>
/// 06 "Endpoints internos". Formato inválido → 401 sem tocar no banco, no throttle nem no log com o valor (o locutor pode trocar os
/// campos e mandar o segredo no lugar do usuário). Identificador que não existe confere contra um hash fictício (tempo constante).
/// Credencial certa fora da janela responde 401 igual, mas não conta no throttle (o BUTT reconecta sozinho antes do programa).
/// </summary>
public sealed class AutenticarNoHarborHandler(
    IConsultasDoHarbor consultas,
    ThrottleDoHarbor throttle,
    IIpHasher ipHasher,
    ILogger<AutenticarNoHarborHandler> logger) : IRequestHandler<AutenticarNoHarborCommand, JanelaDoHarbor?>
{
    public async Task<JanelaDoHarbor?> Handle(AutenticarNoHarborCommand cmd, CancellationToken ct)
    {
        if (!IPAddress.TryParse(cmd.EnderecoIp, out var ip))
        {
            logger.LogWarning("HARBOR_AUTH_RECUSADO motivo {Motivo}", "ip_invalido");
            return null;
        }

        var ipLog = IpAnonimo.De(ip);
        if (!SegredoDeTransmissao.UsuarioValido(cmd.Usuario))
        {
            logger.LogWarning("HARBOR_AUTH_RECUSADO motivo {Motivo} usuario_invalido ({Tamanho} caracteres) ip {Ip}",
                "formato_invalido", cmd.Usuario?.Length ?? 0, ipLog);
            return null;
        }

        var identificador = Guid.ParseExact(cmd.Usuario!, "D");
        if (!SegredoDeTransmissao.SenhaValida(cmd.Senha))
        {
            logger.LogWarning("HARBOR_AUTH_RECUSADO motivo {Motivo} identificador {Identificador} ip {Ip}", "formato_invalido", identificador, ipLog);
            return null;
        }

        var ipHash = ipHasher.Hash(ip);
        if (await throttle.BloqueadoAsync(identificador, ipHash, ct))
        {
            SegredoDeTransmissao.ConferirFicticio(cmd.Senha!);
            logger.LogWarning("HARBOR_AUTH_RECUSADO motivo {Motivo} identificador {Identificador} ip {Ip}", "bloqueado", identificador, ipLog);
            return null;
        }

        var credencial = await consultas.CredencialAtivaAsync(identificador, ct);
        bool confere;
        if (credencial is null)
        {
            SegredoDeTransmissao.ConferirFicticio(cmd.Senha!);
            confere = false;
        }
        else
        {
            confere = SegredoDeTransmissao.Confere(credencial.Salt, credencial.SegredoHash, cmd.Senha!);
        }

        if (!confere)
        {
            await throttle.RegistrarFalhaAsync(identificador, ipHash, ct);
            logger.LogWarning("HARBOR_AUTH_RECUSADO motivo {Motivo} identificador {Identificador} ip {Ip}", "credencial", identificador, ipLog);
            return null;
        }

        var janela = await consultas.JanelaAsync(identificador, ct);
        if (janela is null)
        {
            logger.LogInformation("HARBOR_AUTH_RECUSADO motivo {Motivo} identificador {Identificador} ip {Ip}", "fora_da_janela", identificador, ipLog);
            return null;
        }

        await throttle.LimparAsync(identificador, ipHash, ct);
        await consultas.RegistrarUsoAsync(credencial!.Id, ct);
        logger.LogInformation("HARBOR_AUTH_OK identificador {Identificador} ip {Ip} programa {ProgramaId} fim {FimUtc}",
            identificador, ipLog, janela.ProgramaId, janela.FimUtc);
        return janela;
    }
}

/// <summary><c>GET /internal/harbor/autorizacao/{identificador}</c>. null = 403 (o Liquidsoap derruba o ao vivo).</summary>
public sealed record AutorizacaoDoHarborQuery(string? Identificador) : IRequest<JanelaDoHarbor?>;

public sealed class AutorizacaoDoHarborHandler(IConsultasDoHarbor consultas, ILogger<AutorizacaoDoHarborHandler> logger)
    : IRequestHandler<AutorizacaoDoHarborQuery, JanelaDoHarbor?>
{
    public async Task<JanelaDoHarbor?> Handle(AutorizacaoDoHarborQuery q, CancellationToken ct)
    {
        if (!SegredoDeTransmissao.UsuarioValido(q.Identificador))
        {
            logger.LogWarning("HARBOR_AUTORIZACAO_NEGADA motivo {Motivo} ({Tamanho} caracteres)", "formato_invalido", q.Identificador?.Length ?? 0);
            return null;
        }

        var identificador = Guid.ParseExact(q.Identificador!, "D");
        var janela = await consultas.JanelaAsync(identificador, ct);
        if (janela is null) logger.LogInformation("HARBOR_AUTORIZACAO_NEGADA identificador {Identificador}", identificador);
        return janela;
    }
}
