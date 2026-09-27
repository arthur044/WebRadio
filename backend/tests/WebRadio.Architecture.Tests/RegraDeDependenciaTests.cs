using System.Reflection;
using NetArchTest.Rules;
using WebRadio.Application;
using WebRadio.Domain.Common;
using Xunit;

namespace WebRadio.Architecture.Tests;

/// <summary>
/// E1-F02: garante em CI a regra de dependência de 00-arquitetura.md §2.1, não só por convenção.
/// </summary>
public class RegraDeDependenciaTests
{
    private const string Domain = "WebRadio.Domain";
    private const string Application = "WebRadio.Application";
    private const string Infrastructure = "WebRadio.Infrastructure";
    private const string Api = "WebRadio.Api";
    private const string Worker = "WebRadio.Worker";
    private const string DbContext = "Microsoft.EntityFrameworkCore.DbContext";

    [Fact]
    public void Domain_nao_depende_de_nenhuma_outra_camada()
    {
        var resultado = Types.InAssembly(typeof(Entidade).Assembly)
            .Should()
            .NotHaveDependencyOnAny(Application, Infrastructure, Api, Worker)
            .GetResult();

        Assert.True(resultado.IsSuccessful, DescreverFalhas(resultado));
    }

    [Fact]
    public void Application_nao_depende_de_Infrastructure()
    {
        var resultado = Types.InAssembly(typeof(AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOn(Infrastructure)
            .GetResult();

        Assert.True(resultado.IsSuccessful, DescreverFalhas(resultado));
    }

    [Fact]
    public void Application_nao_depende_de_Api()
    {
        var resultado = Types.InAssembly(typeof(AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOn(Api)
            .GetResult();

        Assert.True(resultado.IsSuccessful, DescreverFalhas(resultado));
    }

    [Fact]
    public void Application_nao_depende_de_Worker()
    {
        var resultado = Types.InAssembly(typeof(AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOn(Worker)
            .GetResult();

        Assert.True(resultado.IsSuccessful, DescreverFalhas(resultado));
    }

    [Fact]
    public void Api_nao_usa_DbContext_fora_da_Infrastructure()
    {
        // Assembly.Load por nome (em vez de typeof(Program)) evita a ambiguidade: Api e Worker
        // têm, cada um, sua própria classe global `Program` (top-level statements).
        var resultado = Types.InAssembly(Assembly.Load(Api))
            .That()
            .DoNotResideInNamespace(Infrastructure)
            .Should()
            .NotHaveDependencyOn(DbContext)
            .GetResult();

        Assert.True(resultado.IsSuccessful, DescreverFalhas(resultado));
    }

    [Fact]
    public void Worker_nao_usa_DbContext_fora_da_Infrastructure()
    {
        var resultado = Types.InAssembly(Assembly.Load(Worker))
            .That()
            .DoNotResideInNamespace(Infrastructure)
            .Should()
            .NotHaveDependencyOn(DbContext)
            .GetResult();

        Assert.True(resultado.IsSuccessful, DescreverFalhas(resultado));
    }

    private static string DescreverFalhas(TestResult resultado) =>
        resultado.FailingTypes is null
            ? "sem detalhes"
            : string.Join(", ", resultado.FailingTypeNames);
}
