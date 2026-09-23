using NovaWalletLedger.Domain.Common;

namespace NovaWalletLedger.Domain.Audit;

/// <summary>
/// Append-only, immutable audit trail. Separate from <see cref="Ledger.LedgerEntry"/>
/// (the customer-facing statement) so that compliance/forensic queries never
/// depend on — or can be confused with — the transaction table. Nothing in
/// this codebase ever issues an UPDATE or DELETE against this table; the EF
/// configuration also denies it at the database-permission/documentation
/// level (see README "Audit trail immutability").
/// </summary>
public sealed class AuditLogEntry : Entity
{
    public Guid WalletId { get; private set; }
    public AuditAction Action { get; private set; }
    public long AmountKobo { get; private set; }
    public long BalanceBeforeKobo { get; private set; }
    public long BalanceAfterKobo { get; private set; }
    public Guid ActorId { get; private set; }
    public string? CorrelationId { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public string? Metadata { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }

    private AuditLogEntry()
    {
    }

    private AuditLogEntry(
        Guid walletId, AuditAction action, long amountKobo, long balanceBeforeKobo, long balanceAfterKobo,
        Guid actorId, string? correlationId, string? idempotencyKey, string? metadata, DateTimeOffset occurredAtUtc)
        : base(Guid.NewGuid())
    {
        WalletId = walletId;
        Action = action;
        AmountKobo = amountKobo;
        BalanceBeforeKobo = balanceBeforeKobo;
        BalanceAfterKobo = balanceAfterKobo;
        ActorId = actorId;
        CorrelationId = correlationId;
        IdempotencyKey = idempotencyKey;
        Metadata = metadata;
        OccurredAtUtc = occurredAtUtc;
    }

    public static AuditLogEntry Create(
        Guid walletId, AuditAction action, long amountKobo, long balanceBeforeKobo, long balanceAfterKobo,
        Guid actorId, DateTimeOffset occurredAtUtc, string? correlationId = null, string? idempotencyKey = null,
        string? metadata = null) =>
        new(walletId, action, amountKobo, balanceBeforeKobo, balanceAfterKobo, actorId, correlationId,
            idempotencyKey, metadata, occurredAtUtc);
}
