using Microsoft.EntityFrameworkCore;
using NovaWalletLedger.Domain.Audit;
using NovaWalletLedger.Domain.Idempotency;
using NovaWalletLedger.Domain.Ledger;
using NovaWalletLedger.Domain.Outbox;
using NovaWalletLedger.Domain.Wallets;

namespace NovaWalletLedger.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<Wallet> Wallets { get; }
    DbSet<LedgerEntry> LedgerEntries { get; }
    DbSet<AuditLogEntry> AuditLogEntries { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
