using NovaWalletLedger.Domain.Common;

namespace NovaWalletLedger.Domain.Outbox;

/// <summary>
/// Transactional outbox entry. Written in the same DB transaction as the
/// business mutation it describes, guaranteeing at-least-once delivery of
/// domain events (e.g. TransferCompleted) without a distributed transaction
/// across the database and a message broker. A background dispatcher polls
/// and publishes unprocessed rows.
/// </summary>
public sealed class OutboxMessage : Entity
{
    public string Type { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public DateTimeOffset? ProcessedAtUtc { get; private set; }
    public int AttemptCount { get; private set; }
    public string? LastError { get; private set; }

    private OutboxMessage()
    {
    }

    private OutboxMessage(string type, string payloadJson, DateTimeOffset occurredAtUtc)
        : base(Guid.NewGuid())
    {
        Type = type;
        PayloadJson = payloadJson;
        OccurredAtUtc = occurredAtUtc;
    }

    public static OutboxMessage Create(string type, string payloadJson, DateTimeOffset nowUtc) =>
        new(type, payloadJson, nowUtc);

    public void MarkProcessed(DateTimeOffset nowUtc)
    {
        ProcessedAtUtc = nowUtc;
    }

    public void MarkFailed(string error)
    {
        AttemptCount++;
        LastError = error;
    }
}
