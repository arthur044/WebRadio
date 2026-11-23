using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WebRadio.Infrastructure.Persistencia;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>#25 (Guardian no #175): só indisponibilidade vira 503; erro de permissão, de objeto, de restrição e de procedure continua 500 com o log completo.</summary>
public class FalhaDeBancoTests
{
    private sealed class DbExceptionQualquer : DbException
    {
        public DbExceptionQualquer() : base("erro de banco qualquer")
        {
        }
    }

    [Theory]
    [InlineData(-2)] // tempo esgotado
    [InlineData(53)]
    [InlineData(64)]
    [InlineData(233)]
    [InlineData(4060)]
    [InlineData(40613)]
    [InlineData(10053)]
    [InlineData(10054)]
    [InlineData(10060)]
    [InlineData(10061)]
    [InlineData(11001)]
    [InlineData(1205)] // vítima de deadlock
    [InlineData(1222)] // tempo esgotado de trava
    public void Numeros_de_conectividade_e_de_retentativa_sao_indisponibilidade(int numero) => Assert.True(FalhaDeBanco.EhNumeroDeIndisponibilidade(numero));

    [Theory]
    [InlineData(229)] // permissão negada: um GRANT que faltou no deploy
    [InlineData(207)] // coluna inexistente
    [InlineData(208)] // objeto inexistente
    [InlineData(547)] // restrição de chave estrangeira
    [InlineData(2627)] // chave duplicada
    [InlineData(2601)]
    [InlineData(245)] // conversão
    [InlineData(50000)] // RAISERROR de procedure
    [InlineData(50001)] // THROW de procedure
    [InlineData(18456)] // login recusado: credencial errada, configuração
    public void Permissao_objeto_restricao_conversao_e_procedure_nao_sao_indisponibilidade(int numero) => Assert.False(FalhaDeBanco.EhNumeroDeIndisponibilidade(numero));

    [Fact]
    public void RetryLimitExceeded_e_indisponibilidade_e_qualquer_outra_excecao_nao_e()
    {
        Assert.True(FalhaDeBanco.Indisponivel(new RetryLimitExceededException("esgotou", new TimeoutException())));
        Assert.False(FalhaDeBanco.Indisponivel(new DbExceptionQualquer())); // DbException genérica NÃO basta
        Assert.False(FalhaDeBanco.Indisponivel(new DbUpdateException("violação", new DbExceptionQualquer())));
        Assert.False(FalhaDeBanco.Indisponivel(new InvalidOperationException("bug")));
        Assert.False(FalhaDeBanco.Indisponivel(new TimeoutException()));
    }

    [Fact]
    public void Numero_sem_SqlException_e_nulo()
    {
        Assert.Null(FalhaDeBanco.Numero(new InvalidOperationException()));
        Assert.Null(FalhaDeBanco.Numero(new RetryLimitExceededException("esgotou", new TimeoutException())));
    }
}
