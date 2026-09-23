using Microsoft.EntityFrameworkCore.Storage;
using NovaWalletLedger.Application.Common.Interfaces;

namespace NovaWalletLedger.Infrastructure.Persistence;

public sealed class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _db;

    public UnitOfWork(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IAppTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        return new AppTransaction(transaction);
    }
}

internal sealed class AppTransaction : IAppTransaction
{
    private readonly IDbContextTransaction _transaction;
    private int _savepointCounter;

    public AppTransaction(IDbContextTransaction transaction)
    {
        _transaction = transaction;
    }

    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        _transaction.CommitAsync(cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken = default) =>
        _transaction.RollbackAsync(cancellationToken);

    public async Task<T> ExecuteInSavepointAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        var savepointName = $"sp_{Interlocked.Increment(ref _savepointCounter)}";
        await _transaction.CreateSavepointAsync(savepointName, cancellationToken);

        try
        {
            var result = await action();
            await _transaction.ReleaseSavepointAsync(savepointName, cancellationToken);
            return result;
        }
        catch
        {
            await _transaction.RollbackToSavepointAsync(savepointName, cancellationToken);
            throw;
        }
    }

    public ValueTask DisposeAsync() => _transaction.DisposeAsync();
}
