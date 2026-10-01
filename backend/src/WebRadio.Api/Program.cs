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
using WebRadio.Api.Features.Auth;
using WebRadio.Api.Infra;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Seguranca;
using Microsoft.AspNetCore.RateLimiting;
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

// Produção exige configuração explícita de rede e hosts (sem isso o stack subiria "aberto" ou quebrado):
//  - Rede:SubnetApp ausente faria todo cliente dividir o IP do nginx (um balde de 100/min só = auto-DoS);
//  - AllowedHosts precisa listar o host público (o nginx repassa Host = PUBLIC_AUTHORITY), `api` e 127.0.0.1.
var subnet = builder.Configuration["Rede:SubnetApp"];
// Prefixo < 8 (ex.: 0.0.0.0/0) confiaria em qualquer X-Forwarded-For e burlaria o limiter por IP.
var subnetValida = System.Net.IPNetwork.TryParse(subnet ?? "", out var rede) && rede.PrefixLength >= 8;
if (!builder.Environment.IsDevelopment())
{
    if (!subnetValida)
        throw new InvalidOperationException("Rede:SubnetApp (CIDR da rede `app`, prefixo ≥ 8) é obrigatória fora de Development.");
    var hosts = builder.Configuration["AllowedHosts"];
    if (string.IsNullOrWhiteSpace(hosts) || hosts.Contains('*'))
        throw new InvalidOperationException("AllowedHosts precisa listar os hosts permitidos (sem '*') fora de Development.");
}

// S-A11: só o proxy da rede `app` pode dizer o IP do cliente; um salto só (nginx). Sem a subnet configurada,
// nenhum proxy é confiável e o X-Forwarded-For é ignorado (falha fechada).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Clear();
    if (subnetValida)
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(subnet!));
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
            ValidIssuer = EmissorDeTokens.Issuer,
            ValidateAudience = true,
            ValidAudience = EmissorDeTokens.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = "role",
            NameClaimType = "name",
            IssuerSigningKeys = ChavesDeValidacao(jwt.Value).ToList(),
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("PodeModerar", p => p.RequireRole("Locutor", "Admin"))
    .AddPolicy("SomenteAdmin", p => p.RequireRole("Admin"))
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// Limite global de 100/min por IP (00-arquitetura.md §164). O IP só serve de chave de partição; nunca é logado.
// O listener interno :8081 é isento: healthcheck e polling do Liquidsoap vêm de poucos IPs fixos.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Login e registro: limite ESTRITO por IP, num balde separado do global (a chave é a mesma IpChave).
    o.AddPolicy(AuthEndpoints.PoliticaLogin, ctx => RateLimitPartition.GetFixedWindowLimiter(IpChave.De(ctx.Connection.RemoteIpAddress),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(AuthEndpoints.PoliticaRegistrar, ctx => RateLimitPartition.GetFixedWindowLimiter(IpChave.De(ctx.Connection.RemoteIpAddress),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromHours(1) }));
    o.OnRejected = async (ctx, ct) =>
    {
        var http = ctx.HttpContext;
        if (http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == AuthEndpoints.PoliticaLogin)
        {
            // Excedeu o limite de login: MESMO 401, mesmo corpo e mesmos headers (sem Retry-After) que credencial
            // errada. Um 429 distinto serviria de oráculo (D21 / S-A12). SEM hash aqui: esta rota roda em TODA requisição
            // rejeitada, e PBKDF2 nela viraria amplificador de DoS (1 IP a 100 req/s saturaria a CPU). Ser limitado
            // depende só do IP, então não há oráculo de timing por conta.
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await http.Response.WriteAsJsonAsync(TratadorDeExcecoes.Credenciais(), options: null, contentType: "application/problem+json", ct);
            return;
        }

        // Retry-After real: o que o limiter informa (a janela de registrar é de 1 h, a global de 1 min).
        var segundos = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var espera) ? (int)Math.Ceiling(espera.TotalSeconds) : 60;
        http.Response.Headers.RetryAfter = Math.Max(1, segundos).ToString(System.Globalization.CultureInfo.InvariantCulture);
    };
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        ctx.Connection.LocalPort == ListenerInterno.PortaInterna
            ? RateLimitPartition.GetNoLimiter("interno")
            : RateLimitPartition.GetFixedWindowLimiter(IpChave.De(ctx.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 100, Window = TimeSpan.FromMinutes(1) }));
});

static IEnumerable<SecurityKey> ChavesDeValidacao(JwtOptions jwt)
{
    // Cada chave com o kid derivado dela mesma; a anterior (se houver) só valida, durante a rotação (05 §5.1).
    foreach (var texto in new[] { jwt.SigningKey, jwt.SigningKeyAnterior })
    {
        if (string.IsNullOrWhiteSpace(texto)) continue;
        var bytes = Base64Url.DecodeFromChars(texto);
        yield return new SymmetricSecurityKey(bytes) { KeyId = EmissorDeTokens.KidDe(bytes) };
    }
}

var app = builder.Build();

if (!subnetValida)
    app.Logger.LogWarning("Rede:SubnetApp ausente: X-Forwarded-For será ignorado (aceitável só em Development).");

app.UseForwardedHeaders();
// Request logging ANTES do exception handler: erros de domínio (409/400) viram resposta tratada e saem como
// Information; só exceção inesperada é logada como Error (uma vez, pelo TratadorDeExcecoes).
app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseLimiteDeCorpoDeAuth();
app.UseRouting();
app.UseListenerInternoGuard();
app.UseRateLimiter();
app.UseAuthentication();
app.UseTrocaDeSenhaObrigatoria();
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
