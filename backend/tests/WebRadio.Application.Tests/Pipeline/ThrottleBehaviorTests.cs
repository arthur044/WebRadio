using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Application.Features.Pedidos;
using WebRadio.Application.Pipeline;
using WebRadio.Domain.Erros;
using Xunit;

namespace WebRadio.Application.Tests.Pipeline;

/// <summary>Fluxo B: <c>Logging → Validation → Throttle → Handler</c>. A entrada inválida (400) não gasta vaga do throttle.</summary>
public class ThrottleBehaviorTests
{
    private sealed class RegraQueContaEBarra(bool barrar) : IThrottleDoRequest<CriarPedidoCommand>
    {
        public int Chamadas { get; private set; }

        public Task AvaliarAsync(CriarPedidoCommand request, CancellationToken ct)
        {
            Chamadas++;
            return barrar ? throw new MuitosPedidosException(TimeSpan.FromSeconds(7)) : Task.CompletedTask;
        }
    }

    private sealed class HandlerContador : IRequestHandler<CriarPedidoCommand, PedidoDto>
    {
        public int Chamadas { get; private set; }

        public Task<PedidoDto> Handle(CriarPedidoCommand request, CancellationToken cancellationToken)
        {
            Chamadas++;
            return Task.FromResult(new PedidoDto(Guid.NewGuid(), "n", "t", "a", null, "Pendente", DateTime.UtcNow, null, null, null, null));
        }
    }

    private static (IMediator Mediator, RegraQueContaEBarra Regra, HandlerContador Handler) Montar(bool barrar)
    {
        var regra = new RegraQueContaEBarra(barrar);
        var handler = new HandlerContador();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IThrottleDoRequest<CriarPedidoCommand>>(regra);
        services.AddSingleton<IRequestHandler<CriarPedidoCommand, PedidoDto>>(handler);
        services.AddSingleton<IValidator<CriarPedidoCommand>, CriarPedidoValidator>();
        services.AddMediatR(c =>
        {
            c.RegisterServicesFromAssemblyContaining<ThrottleBehaviorTests>();
            c.AddOpenBehavior(typeof(ValidationBehavior<,>));
            c.AddOpenBehavior(typeof(ThrottleBehavior<,>));
        });
        return (services.BuildServiceProvider().GetRequiredService<IMediator>(), regra, handler);
    }

    private static CriarPedidoCommand Valido() => PedidosFixtureComando();

    private static CriarPedidoCommand PedidosFixtureComando() => Pedidos.PedidosFixture.Comando();

    [Fact]
    public async Task Comando_valido_passa_pela_regra_e_chega_ao_handler()
    {
        var (mediator, regra, handler) = Montar(barrar: false);

        await mediator.Send(Valido());

        Assert.Equal(1, regra.Chamadas);
        Assert.Equal(1, handler.Chamadas);
    }

    [Fact]
    public async Task Regra_que_barra_impede_o_handler()
    {
        var (mediator, regra, handler) = Montar(barrar: true);

        var erro = await Assert.ThrowsAsync<MuitosPedidosException>(() => mediator.Send(Valido()));

        Assert.Equal(TimeSpan.FromSeconds(7), erro.TenteDeNovoEm);
        Assert.Equal(1, regra.Chamadas);
        Assert.Equal(0, handler.Chamadas);
    }

    [Fact]
    public async Task Entrada_invalida_e_barrada_pela_validacao_antes_do_throttle()
    {
        var (mediator, regra, handler) = Montar(barrar: false);

        await Assert.ThrowsAsync<ValidationException>(() => mediator.Send(Valido() with { NomeOuvinte = "" }));

        Assert.Equal(0, regra.Chamadas);
        Assert.Equal(0, handler.Chamadas);
    }
}
