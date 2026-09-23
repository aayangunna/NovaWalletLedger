using NovaWalletLedger.Domain.Wallets;

namespace NovaWalletLedger.Application.Common.Interfaces;

/// <summary>
/// Pessimistic row-locking for wallets, backed by <c>SELECT ... FOR UPDATE</c>
/// in the infrastructure implementation. Must be called inside an active
/// transaction (see <see cref="IUnitOfWork"/>) — the lock is held until the
/// transaction commits or rolls back.
/// </summary>
public interface IWalletLockService
{
    /// <summary>Locks a single wallet row for update. Throws if not found.</summary>
    Task<Wallet> LockForUpdateAsync(Guid walletId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks two wallet rows for update, always acquiring locks in a fixed,
    /// deterministic order (ascending by Id) regardless of transfer
    /// direction, so two concurrent transfers between the same pair of
    /// wallets in opposite directions can never deadlock.
    /// </summary>
    Task<(Wallet Source, Wallet Destination)> LockPairForUpdateAsync(
        Guid sourceWalletId, Guid destinationWalletId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sum of debits (outbound transfers) posted against the wallet's ledger
    /// since the start of the current West-Africa-Time calendar day. Must be
    /// called while the wallet row is locked so the figure is consistent
    /// with any concurrent debit attempts.
    /// </summary>
    Task<long> GetDebitedTodayKoboAsync(Guid walletId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}
