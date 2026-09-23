using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NovaWalletLedger.Infrastructure.Persistence;

/// <summary>
/// Design-time factory so `dotnet ef migrations add` works without needing
/// the API host running. The connection string here is only used to
/// generate migration SQL — it is never used at runtime.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NOVAWALLET_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=novawallet;Username=novawallet;Password=novawallet";

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();

        return new AppDbContext(optionsBuilder.Options);
    }
}
