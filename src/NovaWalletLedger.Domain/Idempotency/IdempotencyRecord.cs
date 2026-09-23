using NovaWalletLedger.Domain.Common;

namespace NovaWalletLedger.Domain.Idempotency;

/// <summary>
/// Tracks a single Idempotency-Key on the transfer endpoint. A unique DB
/// constraint on <see cref="Key"/> is the source of truth for "claiming" a
/// key under concurrent replay; this entity just carries the payload.
///
/// Lifecycle: a row is inserted (RequestHash set, ResponseStatusCode = null)
/// inside the same transaction that locks the source wallet, before the
/// transfer is processed. If the insert fails with a unique-violation, an
/// existing row is fetched:
///   - same RequestHash, ResponseStatusCode set  -> replay: return stored response
///   - same RequestHash, ResponseStatusCode null  -> another request with the
///     same key is currently in flight -> 409 Conflict ("processing")
///   - different RequestHash                      -> 422/409 key reuse conflict
/// </summary>
public sealed class IdempotencyRecord : Entity
{
    public string Key { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public Guid WalletId { get; private set; }
    public int? ResponseStatusCode { get; private set; }
    public string? ResponseBody { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    private IdempotencyRecord()
    {
    }

    private IdempotencyRecord(string key, string requestHash, Guid walletId, DateTimeOffset createdAtUtc)
        : base(Guid.NewGuid())
    {
        Key = key;
        RequestHash = requestHash;
        WalletId = walletId;
        CreatedAtUtc = createdAtUtc;
    }

    public static IdempotencyRecord StartNew(string key, string requestHash, Guid walletId, DateTimeOffset nowUtc) =>
        new(key, requestHash, walletId, nowUtc);

    public bool IsSameRequest(string requestHash) => RequestHash == requestHash;

    public bool IsCompleted => ResponseStatusCode is not null;

    public void Complete(int statusCode, string responseBody, DateTimeOffset nowUtc)
    {
        ResponseStatusCode = statusCode;
        ResponseBody = responseBody;
        CompletedAtUtc = nowUtc;
    }
}
