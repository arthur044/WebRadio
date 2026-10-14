using System.Globalization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Seguranca;

namespace WebRadio.Api.Infra;

/// <summary>
/// Confere a claim <c>sv</c> depois da assinatura e do prazo (00 §segurança): token de usuário desativado, com papel
/// alterado ou senha trocada deixa de valer em até 15 s (o cache), não em até 15 min (o <c>exp</c>). Vale para todo
/// endpoint autenticado e para o hub. Token sem <c>sub</c>/<c>sv</c> válidos não foi emitido por esta versão: recusado.
/// </summary>
public static class VersaoDeSegurancaDoToken
{
    public static async Task ValidarAsync(TokenValidatedContext contexto)
    {
        var principal = contexto.Principal;
        if (!Guid.TryParse(principal?.FindFirst("sub")?.Value, out var usuarioId)
            || !int.TryParse(principal!.FindFirst(EmissorDeTokens.ClaimVersaoSeguranca)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var versaoDoToken))
        {
            contexto.Fail("Token sem sub/sv.");
            return;
        }

        var versoes = contexto.HttpContext.RequestServices.GetRequiredService<IVersaoDeSeguranca>();
        if (await versoes.AtualAsync(usuarioId, contexto.HttpContext.RequestAborted) != versaoDoToken)
            contexto.Fail("Versão de segurança desatualizada.");
    }
}
