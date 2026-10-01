using System.Reflection;
using MediatR;
using WebRadio.Api.Infra;
using WebRadio.Application;
using Xunit;

namespace WebRadio.Architecture.Tests;

/// <summary>
/// A máscara do Serilog só vale para {@Obj}. Um request com senha/token/e-mail que seja logado com {Obj}
/// imprimiria tudo via ToString(); então todo request sensível precisa redefinir ToString (ou PrintMembers
/// do record) e esconder esses campos (Guardian m3 / Sentinel 2 no PR #7).
/// </summary>
public class RequestsSensiveisTests
{
    [Fact]
    public void Request_com_propriedade_sensivel_redefine_ToString_ou_PrintMembers()
    {
        var violadores = typeof(AssemblyMarker).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IBaseRequest).IsAssignableFrom(t))
            .Where(t => t.GetProperties().Any(p => MascaraSegredosPolicy.EhSensivel(p.Name)))
            .Where(t => t.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, Type.EmptyTypes) is null
                        && t.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) is null)
            .Select(t => t.FullName)
            .ToList();

        Assert.True(violadores.Count == 0, "Requests sensíveis sem ToString/PrintMembers: " + string.Join(", ", violadores));
    }
}
