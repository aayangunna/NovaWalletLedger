using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace NovaWalletLedger.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real API host (Program.cs, including its automatic EF Core
/// migration step) against a disposable Postgres instance started via
/// Testcontainers, so integration tests exercise the exact same locking,
/// idempotency, and daily-limit SQL that runs in production — not an
/// in-memory provider, which does not support SELECT ... FOR UPDATE and
/// would hide real concurrency bugs.
///
/// Requires a running Docker daemon.
/// </summary>
public sealed class NovaWalletApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("novawallet_test")
        .WithUsername("novawallet")
        .WithPassword("novawallet")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = _postgres.GetConnectionString(),
                ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-production-use-32bytes",
                ["Jwt:Issuer"] = "novawallet-mock-issuer",
                ["Jwt:Audience"] = "novawallet-ledger-api"
            });
        });
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        // Touch the server once so Program.cs's own MigrateDatabaseAsync path runs.
        using var _ = CreateClient();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        Dispose();
        await _postgres.DisposeAsync();
    }
}
