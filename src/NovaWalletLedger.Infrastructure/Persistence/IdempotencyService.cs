using Microsoft.EntityFrameworkCore;
using Npgsql;
using NovaWalletLedger.Application.Common.Interfaces;
using NovaWalletLedger.Domain.Idempotency;

namespace NovaWalletLedger.Infrastructure.Persistence;

/// <summary>
/// Claims Idempotency-Key values against a unique DB constraint. When two
/// concurrent requests race to claim the same brand-new key, the loser's
/// INSERT hits a unique-violation; that failure is contained to a savepoint
/// so it does not abort the caller's outer transaction, and the loser then
/// re-reads the winner's row to decide replay/in-flight/conflict.
/// </summary>
public sealed class IdempotencyService : IIdempotencyService
{
    private const string SavepointName = "idempotency_claim";

    private readonly AppDbContext _db;

    public IdempotencyService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IdempotencyClaimResult> TryClaimAsync(
        string key, string requestHash, Guid walletId, CancellationToken cancellationToken = default)
    {
        var existing = await _db.IdempotencyRecords.FirstOrDefaultAsync(r => r.Key == key, cancellationToken);
        if (existing is not null)
            return Evaluate(existing, requestHash);

        var record = IdempotencyRecord.StartNew(key, requestHash, walletId, DateTimeOffset.UtcNow);
        _db.IdempotencyRecords.Add(record);

        var transaction = _db.Database.CurrentTransaction;
        if (transaction is null)
        {
            await _db.SaveChangesAsync(cancellationToken);
            return new IdempotencyClaimResult(IdempotencyClaimOutcome.Claimed);
        }

        await transaction.CreateSavepointAsync(SavepointName, cancellationToken);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync(SavepointName, cancellationToken);
            return new IdempotencyClaimResult(IdempotencyClaimOutcome.Claimed);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await transaction.RollbackToSavepointAsync(SavepointName, cancellationToken);
            _db.Entry(record).State = EntityState.Detached;

            var winner = await _db.IdempotencyRecords
                .AsNoTracking()
                .FirstAsync(r => r.Key == key, cancellationToken);

            return Evaluate(winner, requestHash);
        }
    }

    public async Task CompleteAsync(
        string key, int statusCode, string responseBody, CancellationToken cancellationToken = default)
    {
        var record = await _db.IdempotencyRecords.FirstAsync(r => r.Key == key, cancellationToken);
        record.Complete(statusCode, responseBody, DateTimeOffset.UtcNow);
    }

    private static IdempotencyClaimResult Evaluate(IdempotencyRecord existing, string requestHash)
    {
        if (!existing.IsSameRequest(requestHash))
            return new IdempotencyClaimResult(IdempotencyClaimOutcome.KeyReuseConflict);

        if (!existing.IsCompleted)
            return new IdempotencyClaimResult(IdempotencyClaimOutcome.InFlight);

        return new IdempotencyClaimResult(
            IdempotencyClaimOutcome.Replay, existing.ResponseStatusCode, existing.ResponseBody);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
