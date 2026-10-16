using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using WebRadio.Domain.Erros;

namespace WebRadio.Api.Infra;

/// <summary>
/// <c>If-Match</c> das rotas que alteram um recurso com RowVersion (02, cabeçalho): sem o cabeçalho → 428; valor que não é um
/// ETag forte nosso → 412. O <c>ETag</c> é o RowVersion em base64 entre aspas.
/// </summary>
public static class PreCondicaoIfMatch
{
    /// <summary>Devolve a versão enviada, ou o <see cref="IResult"/> 428 quando o cabeçalho falta.</summary>
    public static byte[]? Exigir(HttpContext http, out IResult? resposta)
    {
        var ifMatch = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            resposta = Results.Problem(new ProblemDetails
            {
                Status = StatusCodes.Status428PreconditionRequired,
                Type = "/erros/precondicao-obrigatoria",
                Title = "Pré-condição obrigatória.",
                Detail = "Envie o cabeçalho If-Match com a versão lida do registro.",
            });
            return null;
        }

        resposta = null;
        // Um If-Match que não é um RowVersion nosso é um valor divergente: 412 (a exceção vira ProblemDetails no tratador).
        return VersaoDo(ifMatch) ?? throw new VersaoDesatualizadaException();
    }

    /// <summary>Aceita um único ETag forte entre aspas (o que a API emite). Lista, <c>*</c>, <c>W/</c> ou base64 inválido → null.</summary>
    public static byte[]? VersaoDo(string ifMatch)
    {
        if (!EntityTagHeaderValue.TryParse(ifMatch, out var etag) || etag.IsWeak) return null;

        var base64 = etag.Tag.ToString().Trim('"');
        var bytes = new byte[base64.Length]; // base64 decodificado nunca é maior que o texto
        return Convert.TryFromBase64String(base64, bytes, out var lidos) ? bytes[..lidos] : null;
    }

    public static void DefinirETag(HttpContext http, string versao) => http.Response.Headers.ETag = $"\"{versao}\"";
}
