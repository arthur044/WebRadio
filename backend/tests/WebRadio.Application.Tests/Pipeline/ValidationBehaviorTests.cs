using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Application;
using Xunit;

namespace WebRadio.Application.Tests.Pipeline;

public class ValidationBehaviorTests
{
    private sealed record Ping(string Nome) : IRequest<string>;

    private sealed class PingValidator : AbstractValidator<Ping>
    {
        public PingValidator() => RuleFor(x => x.Nome).NotEmpty().WithMessage("Nome é obrigatório.");
    }

    private sealed class PingHandler : IRequestHandler<Ping, string>
    {
        public Task<string> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult($"pong {request.Nome}");
    }

    private static IMediator Montar()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        services.AddTransient<IValidator<Ping>, PingValidator>();
        services.AddTransient<IRequestHandler<Ping, string>, PingHandler>();
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    [Fact]
    public async Task Requisicao_invalida_lanca_ValidationException_sem_chamar_o_handler()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => Montar().Send(new Ping("")));
        Assert.Contains(ex.Errors, e => e.PropertyName == "Nome");
    }

    [Fact]
    public async Task Requisicao_valida_chega_ao_handler()
    {
        Assert.Equal("pong ana", await Montar().Send(new Ping("ana")));
    }
}
