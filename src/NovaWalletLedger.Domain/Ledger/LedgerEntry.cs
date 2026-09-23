using NovaWalletLedger.Domain.Common;

namespace NovaWalletLedger.Domain.Ledger;

/// <summary>
/// A single movement against a wallet's balance. This is the queryable
/// transaction/statement table. It is written once and never mutated;
/// corrections happen via new, offsetting entries (never via UPDATE/DELETE).
/// </summary>
public sealed class LedgerEntry : Entity
{
    public Guid WalletId { get; private set; }
    public LedgerEntryType Type { get; private set; }
    public long AmountKobo { get; private set; }
    public long BalanceAfterKobo { get; private set; }
    public string Currency { get; private set; } = "NGN";
    public Guid? CounterpartyWalletId { get; private set; }
    public Guid TransferGroupId { get; private set; }
    public string? Description { get; private set; }
    public string? ExternalReference { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private LedgerEntry()
    {
    }

    private LedgerEntry(
        Guid walletId,
        LedgerEntryType type,
        long amountKobo,
        long balanceAfterKobo,
        string currency,
        Guid? counterpartyWalletId,
        Guid transferGroupId,
        string? description,
        string? externalReference,
        DateTimeOffset createdAtUtc)
        : base(Guid.NewGuid())
    {
        WalletId = walletId;
        Type = type;
        AmountKobo = amountKobo;
        BalanceAfterKobo = balanceAfterKobo;
        Currency = currency;
        CounterpartyWalletId = counterpartyWalletId;
        TransferGroupId = transferGroupId;
        Description = description;
        ExternalReference = externalReference;
        CreatedAtUtc = createdAtUtc;
    }

    public static LedgerEntry ForCredit(
        Guid walletId, Money amount, long balanceAfterKobo, DateTimeOffset nowUtc,
        Guid? counterpartyWalletId = null, Guid? transferGroupId = null,
        string? description = null, string? externalReference = null) =>
        new(walletId, LedgerEntryType.Credit, amount.AmountKobo, balanceAfterKobo, amount.Currency,
            counterpartyWalletId, transferGroupId ?? Guid.NewGuid(), description, externalReference, nowUtc);

    public static LedgerEntry ForDebit(
        Guid walletId, Money amount, long balanceAfterKobo, DateTimeOffset nowUtc,
        Guid? counterpartyWalletId = null, Guid? transferGroupId = null,
        string? description = null, string? externalReference = null) =>
        new(walletId, LedgerEntryType.Debit, amount.AmountKobo, balanceAfterKobo, amount.Currency,
            counterpartyWalletId, transferGroupId ?? Guid.NewGuid(), description, externalReference, nowUtc);
}
