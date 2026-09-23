namespace NovaWalletLedger.Application.Common.Interfaces;

public enum IdempotencyClaimOutcome
{
    /// <summary>No prior record — caller should process the request normally.</summary>
    Claimed,

    /// <summary>Same key + same payload, already completed — return the stored response verbatim.</summary>
    Replay,

    /// <summary>Same key + same payload, still being processed by another in-flight request.</summary>
    InFlight,

    /// <summary>Same key, different payload — must be rejected (409).</summary>
    KeyReuseConflict
}

public sealed record IdempotencyClaimResult(
    IdempotencyClaimOutcome Outcome,
    int? StoredStatusCode = null,
    string? StoredResponseBody = null);

/// <summary>
/// Guarantees "Idempotency-Key" replay-safety on the transfer endpoint.
/// Claiming happens inside the caller's transaction/savepoint so the claim
/// and the transfer either both commit or both roll back together.
/// </summary>
public interface IIdempotencyService
{
    Task<IdempotencyClaimResult> TryClaimAsync(
        string key, string requestHash, Guid walletId, CancellationToken cancellationToken = default);

    Task CompleteAsync(
        string key, int statusCode, string responseBody, CancellationToken cancellationToken = default);
}
