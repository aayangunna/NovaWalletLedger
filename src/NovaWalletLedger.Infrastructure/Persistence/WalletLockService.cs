using Microsoft.EntityFrameworkCore;
using NovaWalletLedger.Application.Common.Exceptions;
using NovaWalletLedger.Application.Common.Interfaces;
using NovaWalletLedger.Domain.Common;
using NovaWalletLedger.Domain.Ledger;
using NovaWalletLedger.Domain.Wallets;

namespace NovaWalletLedger.Infrastructure.Persistence;

/// <summary>
/// Pessimistic locking via Postgres <c>SELECT ... FOR UPDATE</c>. Must run
/// inside an already-open transaction — see <see cref="UnitOfWork"/> — the
/// lock is released automatically when that transaction commits/rolls back.
/// </summary>
public sealed class WalletLockService : IWalletLockService
{
    private readonly AppDbContext _db;

    public WalletLockService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Wallet> LockForUpdateAsync(Guid walletId, CancellationToken cancellationToken = default)
    {
        // xmin is a Postgres *system* column, which "SELECT *" does not
        // include — it must be selected explicitly, or EF's generated
        // wrapper query for the xmin concurrency-token shadow property fails
        // with "column n.xmin does not exist".
        var wallet = await _db.Wallets
            .FromSqlInterpolated($"SELECT *, xmin FROM wallets WHERE id = {walletId} FOR UPDATE")
            .AsTracking()
            .SingleOrDefaultAsync(cancellationToken);

        return wallet ?? throw new WalletNotFoundException(walletId);
    }

    public async Task<(Wallet Source, Wallet Destination)> LockPairForUpdateAsync(
        Guid sourceWalletId, Guid destinationWalletId, CancellationToken cancellationToken = default)
    {
        // A single statement, ordered by id, locks both rows in one round
        // trip and in a globally consistent order — so a concurrent transfer
        // in the opposite direction between the same two wallets acquires
        // its locks in the same relative order and can never deadlock.
        var wallets = await _db.Wallets
            .FromSqlInterpolated($"""
                SELECT *, xmin FROM wallets
                WHERE id = ANY (ARRAY[{sourceWalletId}, {destinationWalletId}]::uuid[])
                ORDER BY id
                FOR UPDATE
                """)
            .AsTracking()
            .ToListAsync(cancellationToken);

        var source = wallets.FirstOrDefault(w => w.Id == sourceWalletId)
            ?? throw new WalletNotFoundException(sourceWalletId);
        var destination = wallets.FirstOrDefault(w => w.Id == destinationWalletId)
            ?? throw new WalletNotFoundException(destinationWalletId);

        return (source, destination);
    }

    public async Task<long> GetDebitedTodayKoboAsync(
        Guid walletId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var startOfDayUtc = WestAfricaClock.StartOfWatDayUtc(nowUtc);

        var total = await _db.LedgerEntries
            .Where(e => e.WalletId == walletId
                        && e.Type == LedgerEntryType.Debit
                        && e.CreatedAtUtc >= startOfDayUtc)
            .SumAsync(e => (long?)e.AmountKobo, cancellationToken);

        return total ?? 0L;
    }
}
