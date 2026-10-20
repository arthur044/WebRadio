using Xunit;

namespace WebRadio.Api.IntegrationTests;

/// <summary>
/// Os <c>ILogger&lt;T&gt;</c> da Api escrevem no <c>Log.Logger</c> estático do Serilog, que cada host de teste sobrescreve ao subir.
/// Classes que varrem os logs rodam nesta coleção, sozinhas: o estático é o do host delas. Usuários de teste
/// são criados pelo host derivado (o que tem o sink), nunca pelo <c>ApiFactory.Services</c> base, que trocaria o estático.
/// </summary>
[CollectionDefinition(Nome, DisableParallelization = true)]
public sealed class ColecaoDeLogs
{
    public const string Nome = "logs-da-requisicao";
}
