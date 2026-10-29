using System.Net;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Comum;
using WebRadio.Application.Pipeline;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Interacao;

namespace WebRadio.Application.Features.Pedidos;

/// <summary>
/// <c>POST /pedidos</c> (02 §5). <c>ListenerId</c> é o <c>X-Listener-Id</c> (a Api manda <c>Guid.Empty</c> quando falta ou não é UUID, e a
/// validação responde 400); <c>Ip</c> é o do socket, já corrigido pelo ForwardedHeaders. Nome, dedicatória e identificadores de ouvinte
/// são PII: o <c>ToString</c> não imprime nada disso.
/// </summary>
public sealed record CriarPedidoCommand(
    Guid ListenerId,
    IPAddress? Ip,
    Guid? UsuarioId,
    string NomeOuvinte,
    string TituloMusica,
    string Artista,
    string? Mensagem) : IRequest<PedidoDto>, IThrottledRequest
{
    public override string ToString() => nameof(CriarPedidoCommand);
}

public sealed class CriarPedidoValidator : AbstractValidator<CriarPedidoCommand>
{
    public const int TamanhoNome = 60;
    public const int TamanhoTitulo = 200;
    public const int TamanhoArtista = 200;
    public const int TamanhoMensagem = 280;

    public CriarPedidoValidator()
    {
        RuleFor(x => x.ListenerId).NotEmpty().OverridePropertyName("X-Listener-Id")
            .WithMessage("X-Listener-Id precisa ser um UUID válido.");
        RuleFor(x => x.NomeOuvinte).TextoSeguro(TamanhoNome);
        RuleFor(x => x.TituloMusica).TextoSeguro(TamanhoTitulo);
        RuleFor(x => x.Artista).TextoSeguro(TamanhoArtista);
        // Dedicatória opcional e de linha única (07, E3-D7): vazia vira "sem dedicatória"; só com espaços é erro.
        When(x => x.Mensagem is { Length: > 0 }, () => RuleFor(x => x.Mensagem).TextoSeguro(TamanhoMensagem));
    }
}

/// <summary>
/// Fluxo B passo 5: grava <c>Pendente</c> com o <c>ProgramaId</c> da janela (E3-D1) e o <c>PedidoCriado</c> no outbox para
/// <c>locutores</c>, na mesma transação. Com o Redis fora (<see cref="EstadoDoThrottle"/>), o anti-spam roda aqui, no SQL, dentro da
/// transação do <c>INSERT</c> e sob <c>sp_getapplock</c> do dispositivo: o fallback é verifica-e-insere e, sem a trava, duas requisições
/// simultâneas do mesmo dispositivo passariam. Trava ocupada além de 2 s → 429 com <c>Retry-After</c> de 1 s.
/// </summary>
public sealed class CriarPedidoHandler(
    IRadioDbContext db,
    ITransacaoComTrava transacao,
    IConsultasDePedidos consultas,
    IListenerIdentity identidade,
    IOutbox outbox,
    IClock clock,
    EstadoDoThrottle estado,
    IOptions<PedidosOptions> opcoes) : IRequestHandler<CriarPedidoCommand, PedidoDto>
{
    public static readonly TimeSpan EsperaDaTrava = TimeSpan.FromMilliseconds(2000);

    public async Task<PedidoDto> Handle(CriarPedidoCommand cmd, CancellationToken ct)
    {
        var dispositivo = identidade.DeviceHash(cmd.ListenerId);
        var ip = identidade.IpHash(cmd.Ip);
        var programaId = await consultas.ProgramaDaJanelaAsync(ct);

        if (!estado.UsarFallbackSql)
            return await GravarAsync(cmd, dispositivo, ip, programaId, ct);

        try
        {
            return await transacao.ExecutarAsync(Travas.PedidoDoDispositivo(dispositivo), EsperaDaTrava, async ct2 =>
            {
                var espera = await consultas.EsperaDoFallbackAsync(dispositivo, ip, cmd.UsuarioId, opcoes.Value.LimitePorIp, ct2);
                if (espera.Maior() is { } tempo) throw new MuitosPedidosException(tempo);
                return await GravarAsync(cmd, dispositivo, ip, programaId, ct2);
            }, ct);
        }
        catch (TravaOcupadaException)
        {
            throw new MuitosPedidosException(TimeSpan.FromSeconds(1));
        }
    }

    private async Task<PedidoDto> GravarAsync(CriarPedidoCommand cmd, byte[] dispositivo, byte[] ip, Guid? programaId, CancellationToken ct)
    {
        var mensagem = string.IsNullOrEmpty(cmd.Mensagem) ? null : TextoSeguro.Normalizar(cmd.Mensagem);
        var pedido = new PedidoMusica(
            Guid.CreateVersion7(), programaId, cmd.UsuarioId,
            TextoSeguro.Normalizar(cmd.NomeOuvinte), TextoSeguro.Normalizar(cmd.TituloMusica), TextoSeguro.Normalizar(cmd.Artista), mensagem,
            dispositivo, ip, clock.UtcNow);

        db.PedidosMusica.Add(pedido);
        var dto = PedidoDto.De(pedido);
        outbox.EnfileirarParaGrupos([GruposDoHub.Locutores], EventosDoOutbox.PedidoCriado, dto);
        await db.SaveChangesAsync(ct);
        return dto;
    }
}
