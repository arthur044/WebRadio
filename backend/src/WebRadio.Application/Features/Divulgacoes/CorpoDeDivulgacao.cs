using System.Text.RegularExpressions;
using FluentValidation;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Comum;
using WebRadio.Domain.Interacao;

namespace WebRadio.Application.Features.Divulgacoes;

/// <summary>O corpo do <c>POST</c> e do <c>PUT</c> (02 §7): o mesmo, e o <c>PUT</c> substitui o recurso inteiro (<c>imagemChave</c> ausente = sem imagem).</summary>
public interface ICorpoDeDivulgacao
{
    string Titulo { get; }

    string Mensagem { get; }

    string? ImagemChave { get; }

    string? LinkDestino { get; }

    byte Prioridade { get; }

    DateTime? InicioExibicaoUtc { get; }

    DateTime? FimExibicaoUtc { get; }

    bool Ativo { get; }
}

public static partial class ChaveDeImagem
{
    public const string Prefixo = "divulgacoes/";

    /// <summary>
    /// 07 E3-D5: <c>divulgacoes/{guid:N}.webp</c>, GUID aleatório (nunca sequencial: o E3-D6 depende de a chave ser imprevisível). Sem
    /// <c>IgnoreCase</c>, e <c>\z</c> (o <c>$</c> aceitaria um <c>\n</c> final).
    /// </summary>
    [GeneratedRegex(@"^divulgacoes/[0-9a-f]{32}\.webp\z")]
    public static partial Regex Valida();

    public static string Nova() => $"{Prefixo}{Guid.NewGuid():N}.webp";
}

/// <summary>As regras de entrada do corpo: textos pelo <see cref="TextoSeguro"/> (E3-D7), link pelo S-B03, chave pelo E3-D5.</summary>
public abstract class CorpoDeDivulgacaoValidator<T> : AbstractValidator<T> where T : ICorpoDeDivulgacao
{
    public const int TamanhoTitulo = 120;
    public const int TamanhoMensagem = 1000;

    protected CorpoDeDivulgacaoValidator()
    {
        RuleFor(x => x.Titulo).TextoSeguro(TamanhoTitulo);
        // A mensagem aceita \n (E3-D7) e é texto puro: o frontend nunca a renderiza como HTML.
        RuleFor(x => x.Mensagem).TextoSeguro(TamanhoMensagem, aceitaQuebraDeLinha: true);
        RuleFor(x => x.Prioridade).LessThanOrEqualTo(Divulgacao.PrioridadeMaxima).WithMessage($"A prioridade vai de 0 a {Divulgacao.PrioridadeMaxima}.");

        When(x => x.ImagemChave is not null, () =>
            RuleFor(x => x.ImagemChave!).Must(c => ChaveDeImagem.Valida().IsMatch(c)).WithMessage("A chave da imagem não é uma chave devolvida pelo envio de imagens."));

        When(x => x.LinkDestino is not null, () =>
            RuleFor(x => x.LinkDestino!).Must(l => LinkDeDivulgacao.Normalizar(l) is not null)
                .WithMessage("O link precisa ser https:// com um endereço público (sem IP, localhost, .local, .internal, usuário ou espaço) de até 2048 caracteres."));

        RuleFor(x => x.InicioExibicaoUtc).Must(d => d is null || d.Value.Kind == DateTimeKind.Utc).WithMessage("O horário precisa estar em UTC (terminar em Z).")
            .Must(d => d is null || d.Value.Ticks % TimeSpan.TicksPerSecond == 0).WithMessage("O horário tem precisão de segundo.");
        RuleFor(x => x.FimExibicaoUtc).Must(d => d is null || d.Value.Kind == DateTimeKind.Utc).WithMessage("O horário precisa estar em UTC (terminar em Z).")
            .Must(d => d is null || d.Value.Ticks % TimeSpan.TicksPerSecond == 0).WithMessage("O horário tem precisão de segundo.");
        When(x => x.InicioExibicaoUtc is not null && x.FimExibicaoUtc is not null, () =>
            RuleFor(x => x.FimExibicaoUtc!.Value).GreaterThan(x => x.InicioExibicaoUtc!.Value).WithName("fimExibicaoUtc")
                .WithMessage("O fim da exibição precisa ser depois do início."));
    }
}

/// <summary>A imagem tem de existir no bucket e não estar ligada a OUTRA divulgação (409): a conferência dos dois lados do E3-D5.</summary>
internal static class ImagemDeDivulgacao
{
    public static async Task GarantirUsavelAsync(IObjectStorage storage, Abstracoes.IRadioDbContext db, string chave, Guid? divulgacaoId, CancellationToken ct)
    {
        if (await storage.HeadAsync(Buckets.Publico, chave, ct) is null)
            throw new ValidationException([new FluentValidation.Results.ValidationFailure("ImagemChave", "A imagem não existe: envie-a de novo.")]);

        var emUso = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
            db.Divulgacoes, d => d.ImagemChaveStorage == chave && d.Id != divulgacaoId, ct);
        if (emUso) throw new Domain.Erros.ImagemEmUsoException();
    }
}
