using Microsoft.Extensions.DependencyInjection;
using WebRadio.Application;
using WebRadio.Application.Autenticacao;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

public class LifetimeTests
{
    /// <summary>
    /// LoginBloqueio guarda o estado "Redis indisponivel" em um campo. Em singleton, a primeira queda desligaria o bloqueio de
    /// login para sempre (ate reiniciar). O lifetime precisa continuar Scoped.
    /// </summary>
    [Fact]
    public void LoginBloqueio_precisa_ser_Scoped()
    {
        var services = new ServiceCollection();
        services.AddApplication();

        var descritor = Assert.Single(services, s => s.ServiceType == typeof(LoginBloqueio));

        Assert.Equal(ServiceLifetime.Scoped, descritor.Lifetime);
    }
}
