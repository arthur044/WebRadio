using Microsoft.Extensions.Options;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Midia;

namespace WebRadio.Application.Features.Midias;

/// <summary>
/// Seção <c>Midia</c> (01 §2.5, 02 §6, 08 E4-F02). Os limites por tipo vêm do 01: Vinheta 10 MiB e 1 a 60 s, Comercial 20 MiB e 5 a 120 s, Música 250 MiB
/// e 30 s a 20 min. <c>UploadsPorHora</c> e <c>MaximoAbertos</c> são os limites do S-M06 (por usuário).
/// </summary>
public sealed class MidiaOptions
{
    public const string Secao = "Midia";

    private const long MiB = 1024 * 1024;

    public const int UploadsPorHoraPadrao = 20;
    public const int MaximoAbertosPadrao = 5;

    public int UploadsPorHora { get; set; } = UploadsPorHoraPadrao;

    /// <summary>Uploads em <c>AguardandoUpload</c>, <c>Pendente</c> ou <c>EmAnalise</c> por usuário.</summary>
    public int MaximoAbertos { get; set; } = MaximoAbertosPadrao;

    public LimiteDoTipo Vinheta { get; set; } = new() { TamanhoMaximoBytes = 10 * MiB, DuracaoMinimaSegundos = 1, DuracaoMaximaSegundos = 60 };

    public LimiteDoTipo Comercial { get; set; } = new() { TamanhoMaximoBytes = 20 * MiB, DuracaoMinimaSegundos = 5, DuracaoMaximaSegundos = 120 };

    public LimiteDoTipo Musica { get; set; } = new() { TamanhoMaximoBytes = 250 * MiB, DuracaoMinimaSegundos = 30, DuracaoMaximaSegundos = 20 * 60 };

    public LimiteMidia Limite(TipoMidia tipo)
    {
        var l = tipo switch
        {
            TipoMidia.Vinheta => Vinheta,
            TipoMidia.Comercial => Comercial,
            TipoMidia.Musica => Musica,
            _ => throw new ArgumentOutOfRangeException(nameof(tipo)),
        };
        return new LimiteMidia(l.TamanhoMaximoBytes, TimeSpan.FromSeconds(l.DuracaoMinimaSegundos), TimeSpan.FromSeconds(l.DuracaoMaximaSegundos));
    }
}

public sealed class LimiteDoTipo
{
    public long TamanhoMaximoBytes { get; set; }

    public int DuracaoMinimaSegundos { get; set; }

    public int DuracaoMaximaSegundos { get; set; }
}

/// <summary>Valor fora da faixa falha o boot. O teto de tamanho nunca passa do CHECK do banco (<c>CK_ArquivoMidia_Tamanho</c>, 250 MiB).</summary>
public sealed class MidiaOptionsValidator : IValidateOptions<MidiaOptions>
{
    public const long TetoDoBanco = 262_144_000;

    public ValidateOptionsResult Validate(string? name, MidiaOptions o)
    {
        var erros = new List<string>();
        if (o.UploadsPorHora is < 1 or > 1000) erros.Add("Midia:UploadsPorHora deve estar entre 1 e 1000.");
        if (o.MaximoAbertos is < 1 or > 100) erros.Add("Midia:MaximoAbertos deve estar entre 1 e 100.");
        foreach (var (nome, l) in new[] { ("Vinheta", o.Vinheta), ("Comercial", o.Comercial), ("Musica", o.Musica) })
        {
            if (l.TamanhoMaximoBytes is < 1 or > TetoDoBanco) erros.Add($"Midia:{nome}:TamanhoMaximoBytes deve estar entre 1 e {TetoDoBanco}.");
            if (l.DuracaoMinimaSegundos < 1 || l.DuracaoMaximaSegundos < l.DuracaoMinimaSegundos)
                erros.Add($"Midia:{nome}: a duração mínima é de ao menos 1 s e não pode passar da máxima.");
        }

        return erros.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(erros);
    }
}
