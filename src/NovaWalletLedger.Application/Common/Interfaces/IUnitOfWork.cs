namespace NovaWalletLedger.Application.Common.Interfaces;

public interface IUnitOfWork
{
    /// <summary>
    /// Begins a database transaction. Serializable-enough for our purposes
    /// because correctness is enforced by explicit row locks
    /// (see <see cref="IWalletLockService"/>), not by the isolation level alone.
    /// </summary>
    Task<IAppTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}

public interface IAppTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
    Task RollbackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="action"/> inside a nested savepoint so that a
    /// failure inside it (e.g. a unique-constraint violation while claiming
    /// an idempotency key) does not poison/abort the outer transaction.
    /// </summary>
    Task<T> ExecuteInSavepointAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default);
}
