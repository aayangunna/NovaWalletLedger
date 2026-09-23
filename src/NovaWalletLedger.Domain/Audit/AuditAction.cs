namespace NovaWalletLedger.Domain.Audit;

public enum AuditAction
{
    WalletCreated = 0,
    Credit = 1,
    Debit = 2,
    TransferRejectedInsufficientFunds = 3,
    TransferRejectedDailyLimit = 4,
    TransferRejectedIdempotencyConflict = 5
}
