using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NovaWalletLedger.Application.Common.Interfaces;
using NovaWalletLedger.Domain.Audit;
using NovaWalletLedger.Domain.Idempotency;
using NovaWalletLedger.Domain.Ledger;
using NovaWalletLedger.Domain.Outbox;
using NovaWalletLedger.Domain.Wallets;

namespace NovaWalletLedger.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext, IApplicationDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        base.OnModelCreating(modelBuilder);
    }
}
