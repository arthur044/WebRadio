namespace WebRadio.Api.Seguranca;

/// <summary>Teto de corpo por endpoint. O padrao do Kestrel (30 MB) deixaria um JSON gigante ser lido antes da validacao.</summary>
public sealed class LimiteDeCorpoAttribute(long bytes) : Attribute
{
    public long Bytes { get; } = bytes;
}

public static class LimiteDeCorpo
{
    public static IApplicationBuilder UseLimiteDeCorpo(this IApplicationBuilder app) =>
        app.Use(async (contexto, proximo) =>
        {
            var limite = contexto.GetEndpoint()?.Metadata.GetMetadata<LimiteDeCorpoAttribute>();
            var feature = contexto.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();

            if (limite is not null && feature is { IsReadOnly: false })
            {
                feature.MaxRequestBodySize = limite.Bytes;
            }

            await proximo();
        });
}
