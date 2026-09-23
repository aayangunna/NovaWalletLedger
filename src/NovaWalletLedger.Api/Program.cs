using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using NovaWalletLedger.Api.Endpoints.Auth;
using NovaWalletLedger.Api.Endpoints.Wallets;
using NovaWalletLedger.Api.Middleware;
using NovaWalletLedger.Application;
using NovaWalletLedger.Infrastructure;
using NovaWalletLedger.Infrastructure.Auth;
using NovaWalletLedger.Infrastructure.Persistence;
using Serilog;
using Serilog.Context;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] ({TraceId}) {SourceContext}: {Message:lj}{NewLine}{Exception}"));

// ---- Application / Infrastructure ----
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// ---- Authentication / Authorization ----
// JwtBearerOptions is bound lazily from IOptions<JwtOptions> (via the
// AddOptions<T>().Configure<TDep>() pattern) rather than by reading
// builder.Configuration into a local variable here. WebApplicationFactory
// (used by the integration tests) applies its configuration overrides at
// builder.Build() time, which runs *after* this point in a minimal-hosting
// Program.cs — capturing a JwtOptions snapshot this early silently ignores
// those overrides, so the issuer (JwtTokenService, resolved later via DI)
// and the validator would sign/verify with two different keys.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearerOptions, jwtOptionsAccessor) =>
    {
        var jwtOptions = jwtOptionsAccessor.Value;
        bearerOptions.MapInboundClaims = false;
        bearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub"
        };
    });

// No role tiers: every authenticated customer can call every capability,
// scoped only by wallet ownership (enforced per-handler, not via policies).
builder.Services.AddAuthorization();

// ---- Rate limiting (stretch goal): protects the transfer endpoint from abuse ----
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(RateLimiterPolicies.Transfer, httpContext =>
    {
        var partitionKey = httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            // Sized to comfortably clear the concurrency load test's burst
            // (50 simultaneous transfer requests from one customer) while
            // still throttling sustained abuse/flooding across windows —
            // the ₦500k/day limit is the primary backstop against abusive
            // money movement, not this counter.
            PermitLimit = 100,
            Window = TimeSpan.FromSeconds(10),
            QueueLimit = 0
        });
    });
});

// ---- API explorer / Swagger ----
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "NovaWallet Ledger API",
        Version = "v1",
        Description = "Simplified wallet ledger service for FirstBank NovaPay's NovaWallet module. " +
                      "All monetary amounts are integers denominated in kobo (1 NGN = 100 kobo)."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. " +
                      "Get a token from POST /api/auth/token (mock issuer — not for production).",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// ---- Exception handling -> RFC 7807 Problem Details ----
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// Apply EF Core migrations automatically so `docker compose up` is the only
// command needed to get a running, schema-ready service.
await MigrateDatabaseAsync(app);

app.Use(async (context, next) =>
{
    using (LogContext.PushProperty("TraceId", context.TraceIdentifier))
    {
        await next();
    }
});

app.UseExceptionHandler();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "NovaWallet Ledger API v1");
});

app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapTokenEndpoint();
app.MapWalletEndpoints();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = _ => true });

app.Run();

static async Task MigrateDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    const int maxAttempts = 10;
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            await db.Database.MigrateAsync();
            logger.LogInformation("Database migrations applied successfully");
            return;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            logger.LogWarning(ex, "Database not ready (attempt {Attempt}/{MaxAttempts}), retrying in 3s...", attempt, maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
}

// Exposed for WebApplicationFactory<Program> in integration tests.
public partial class Program;
