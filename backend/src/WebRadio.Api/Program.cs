using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using Serilog;
using WebRadio.Api.Configuracao;
using WebRadio.Api.Endpoints;
using WebRadio.Api.Erros;
using WebRadio.Api.Saude;
using WebRadio.Api.Seguranca;
using WebRadio.Application;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// 05 §5.3: segredos de produção chegam como arquivos em /run/secrets (Jwt__SigningKey → Jwt:SigningKey).
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

builder.ConfigurarSerilog();
builder.ConfigurarListeners();

builder.Services
    .AddEncaminhamentoSeguro(builder.Configuration)
    .AddLimitesWebRadio()
    .AddApplication()
    .AddInfrastructureApi(builder.Configuration, builder.Environment)
    .AddAutenticacaoWebRadio()
    .AddSaudeWebRadio(builder.Configuration);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ExcecoesGlobais>();
builder.Services.AddOpenApi();

var app = builder.Build();

// Ordem importa: o IP real precisa estar resolvido antes do log, do rate limit e do bloqueio de login.
app.UseForwardedHeaders();
app.UseSerilogRequestLogging(); // só o path: nunca a query string (S-M09)
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.UseSomenteListenerInterno();
app.UseLimiteDeCorpo();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapAuth();
app.MapSaude();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

await using (var escopo = app.Services.CreateAsyncScope())
{
    foreach (var inicializador in escopo.ServiceProvider.GetServices<IInicializadorDeDados>())
    {
        await inicializador.ExecutarAsync();
    }
}

app.Run();

// Ponto de entrada exposto para WebApplicationFactory<Program> nos testes de integracao (E1-F06+).
public partial class Program;
