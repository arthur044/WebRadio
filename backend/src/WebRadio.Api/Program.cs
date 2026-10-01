using System.Buffers.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Formatting.Compact;
using WebRadio.Api.Infra;
using WebRadio.Api.Segredos;
using WebRadio.Application;
using WebRadio.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Segredos por arquivo (Docker secrets, 05 §5.3): /run/secrets/Jwt__SigningKey vira Jwt:SigningKey.
// Entra depois das variáveis de ambiente, então o arquivo vence — mesmo código em dev e prod.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

// Serilog JSON; request logging registra o path SEM a query string (S-M09: access_token na query do hub).
builder.Host.UseSerilog((ctx, cfg) => cfg
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Destructure.With<MascaraSegredosPolicy>()
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

// Fail-fast de segredos (S-C02): o host RECUSA subir com segredo vazio, __GERAR__ ou curto.
builder.Services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.Secao).ValidateOnStart();
builder.Services.AddOptions<SegurancaOptions>().BindConfiguration(SegurancaOptions.Secao).ValidateOnStart();
builder.Services.AddOptions<PlayoutOptions>().BindConfiguration(PlayoutOptions.Secao).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<SegurancaOptions>, SegurancaOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<PlayoutOptions>, PlayoutOptionsValidator>();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<TratadorDeExcecoes>();
builder.Services.AddEndpointModules();
builder.Services.AddOpenApi();

// S-A11: só o proxy da rede `app` pode dizer o IP do cliente; um salto só (nginx). Sem a subnet configurada,
// nenhum proxy é confiável e o X-Forwarded-For é ignorado (falha fechada).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Clear();
    var subnet = builder.Configuration["Rede:SubnetApp"];
    if (!string.IsNullOrWhiteSpace(subnet))
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(subnet));
});

// JWT (parâmetros do 05 §6). Emissão, refresh e políticas entram na F07; aqui só a validação que a
// FallbackPolicy exige para devolver 401 em vez de estourar sem esquema de autenticação.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((o, jwt) =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateIssuer = true,
            ValidIssuer = "webradio-api",
            ValidateAudience = true,
            ValidAudience = "webradio",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = "role",
            NameClaimType = "name",
            IssuerSigningKey = new SymmetricSecurityKey(Base64Url.DecodeFromChars(jwt.Value.SigningKey!)),
        };
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// Limite global de 100/min por IP (00-arquitetura.md §164). O IP só serve de chave de partição; nunca é logado.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 100, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapEndpointModules();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

app.Run();

// Ponto de entrada exposto para WebApplicationFactory<Program> nos testes de integracao.
public partial class Program;
