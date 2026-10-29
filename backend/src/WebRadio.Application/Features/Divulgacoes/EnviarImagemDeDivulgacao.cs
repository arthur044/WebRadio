using FluentValidation;
using FluentValidation.Results;
using MediatR;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Application.Features.Divulgacoes;

/// <summary>
/// <c>POST /divulgacoes/imagens</c> depois de a Api ter conferido Admin, o limite de 30/h e o tamanho. Passo 1: cabeçalho (formato, 1 quadro, lado
/// ≤ 4096 px) SEM decodificar; nos recusados o decodificador NÃO é chamado. Passo 2: recodifica para WebP sem metadado. Passo 3: grava em
/// <c>publico/divulgacoes/{guid}.webp</c> com <c>Content-Type</c> do servidor. A chave devolvida só vale se for ligada a uma divulgação (E3-D6).
/// </summary>
public sealed record EnviarImagemDeDivulgacaoCommand(byte[] Bytes) : IRequest<ImagemEnviada>
{
    // O conteúdo do arquivo não vai para log.
    public override string ToString() => nameof(EnviarImagemDeDivulgacaoCommand);
}

public sealed record ImagemEnviada(string Chave, string ImagemUrl);

public sealed class EnviarImagemDeDivulgacaoHandler(IValidadorDeCabecalhoDeImagem validador, IRecodificadorDeImagem recodificador, IObjectStorage? storage = null)
    : IRequestHandler<EnviarImagemDeDivulgacaoCommand, ImagemEnviada>
{
    public async Task<ImagemEnviada> Handle(EnviarImagemDeDivulgacaoCommand cmd, CancellationToken ct)
    {
        var cabecalho = validador.Validar(cmd.Bytes);
        if (!cabecalho.Aceita) throw Recusada(cabecalho.Motivo);

        // Storage é opcional em Development (sem Storage:Endpoint não há IObjectStorage; fora dele a Api exige a seção inteira no boot).
        var destino = storage ?? throw new InvalidOperationException("Storage não configurado.");
        byte[] webp;
        try
        {
            webp = await recodificador.RecodificarParaWebpAsync(cmd.Bytes, ct);
        }
        catch (ImagemIlegivelException)
        {
            // Arquivo truncado ou corrompido: erro do arquivo, não do servidor.
            throw Recusada(MotivoDaImagemRecusada.Ilegivel);
        }

        var chave = ChaveDeImagem.Nova();
        await using var fluxo = new MemoryStream(webp, writable: false);
        await destino.PutAsync(Buckets.Publico, chave, fluxo, webp.Length, "image/webp", ct);
        return new ImagemEnviada(chave, destino.UrlPublica(Buckets.Publico, chave));
    }

    private static ValidationException Recusada(MotivoDaImagemRecusada? motivo) => new([new ValidationFailure("file", motivo switch
    {
        MotivoDaImagemRecusada.FormatoNaoAceito => "Só JPEG ou PNG, reconhecidos pelo conteúdo do arquivo.",
        MotivoDaImagemRecusada.Animada => "Imagem animada não é aceita.",
        MotivoDaImagemRecusada.Dimensoes => $"A imagem pode ter no máximo {LimitesDaImagem.LadoMaximo} px de largura e de altura.",
        _ => "Não consegui ler a imagem.",
    })]);
}
