using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Application.Features.Transmissao;

/// <summary>
/// 06 "Revogação automática": desativar a conta, trocar a senha ou ir para <c>Ouvinte</c> revoga a credencial de transmissão, no
/// mesmo SaveChanges da mudança. Troca entre Admin e Locutor não revoga: os dois podem transmitir.
/// </summary>
public sealed class RevogarCredencialAoAlterarCredenciais(
    IRadioDbContext db,
    IClock clock,
    ILogger<RevogarCredencialAoAlterarCredenciais> logger) : IDomainEventHandler<UsuarioCredenciaisAlteradas>
{
    public async Task HandleAsync(UsuarioCredenciaisAlteradas evento, CancellationToken ct)
    {
        if (!await DeveRevogarAsync(evento, ct)) return;

        var ativa = await db.CredenciaisTransmissao.FirstOrDefaultAsync(c => c.UsuarioId == evento.UsuarioId && c.RevogadaEmUtc == null, ct);
        if (ativa is null) return;

        ativa.Revogar(clock.UtcNow);
        logger.LogInformation("CREDENCIAL_TRANSMISSAO_REVOGADA usuario {UsuarioId} identificador {Identificador} motivo {Motivo}",
            evento.UsuarioId, ativa.Identificador, evento.Motivo);
    }

    private async Task<bool> DeveRevogarAsync(UsuarioCredenciaisAlteradas evento, CancellationToken ct)
    {
        if (evento.Motivo != MotivoAlteracaoCredenciais.RoleAlterada) return true;

        // O usuário já está rastreado (o evento saiu dele neste SaveChanges): FindAsync devolve o estado novo, sem ir ao banco.
        var usuario = await db.Usuarios.FindAsync([evento.UsuarioId], ct);
        return usuario is null || usuario.Role is not (Role.Locutor or Role.Admin);
    }
}
