using WebRadio.Domain.Enums;

namespace WebRadio.Application.Abstracoes;

/// <summary>Recodifica o áudio enviado (S-C01). Só o contrato no Épico 1; a implementação (ffmpeg isolado) é do Épico 4.</summary>
public interface IAudioSanitizer
{
    Task<ResultadoSanitizacao> SanitizarAsync(Stream original, TipoMidia tipo, CancellationToken cancellationToken = default);
}

public sealed record ResultadoSanitizacao(bool Aprovado, string? MotivoRejeicao, Stream? Saida, int? DuracaoSegundos);
