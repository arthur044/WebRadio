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

    /// <summary>
    /// Prefixo de namespace, não só o tipo `DbContext`: pega também `DbSet&lt;T&gt;`, os operadores
    /// `IQueryable` de EF e qualquer tipo do pacote — o que faltava no H3 original, que só casava o nome
    /// exato do tipo `DbContext` e deixava passar `AppDbContext`/`DbSet&lt;T&gt;` sem detecção.
    /// </summary>
    private const string EfCore = "Microsoft.EntityFrameworkCore";

    [Fact]
    public void Domain_nao_depende_de_nenhuma_outra_camada()
    {
        var resultado = Types.InAssembly(typeof(Entidade).Assembly)
            .Should()
            .NotHaveDependencyOnAny(Application, Infrastructure, Api, Worker, "MediatR", EfCore)
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

    /// <summary>
    /// "Api não acessa dados diretamente": nenhum tipo do assembly Api pode depender de EF Core — nem do
    /// `DbContext` genérico, nem de um futuro `RadioDbContext`/`DbSet&lt;T&gt;` da Infrastructure. A versão
    /// anterior filtrava com `.That().DoNotResideInNamespace(Infrastructure)`, que não exclui nada (nenhum
    /// tipo do assembly Api reside no namespace da Infrastructure) e checava só o nome exato do tipo
    /// `DbContext`, que não pega uma classe derivada (`AppDbContext`) nem `DbSet&lt;T&gt;`.
    /// </summary>
    [Fact]
    public void Api_nao_depende_de_EfCore()
    {
        // Assembly.Load por nome (em vez de typeof(Program)) evita a ambiguidade: Api e Worker
        // têm, cada um, sua própria classe global `Program` (top-level statements).
        var resultado = Types.InAssembly(Assembly.Load(Api))
            .Should()
            .NotHaveDependencyOn(EfCore)
            .GetResult();

        Assert.True(resultado.IsSuccessful, DescreverFalhas(resultado));
    }

    [Fact]
    public void Worker_nao_depende_de_EfCore()
    {
        var resultado = Types.InAssembly(Assembly.Load(Worker))
            .Should()
            .NotHaveDependencyOn(EfCore)
            .GetResult();

        Assert.True(resultado.IsSuccessful, DescreverFalhas(resultado));
    }

    private static string DescreverFalhas(TestResult resultado) =>
        resultado.FailingTypes is null
            ? "sem detalhes"
            : string.Join(", ", resultado.FailingTypeNames);
}
