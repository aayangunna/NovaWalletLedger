using NovaWalletLedger.Domain.Common;

namespace NovaWalletLedger.Domain.Wallets;

/// <summary>
/// Aggregate root for a customer's NovaWallet balance. All balance mutations
/// must go through <see cref="Credit"/> or <see cref="Debit"/> so the
/// non-negative-balance and daily-limit invariants can never be bypassed.
///
/// Concurrency safety is a two-layer defense:
///  1. The row is pessimistically locked (SELECT ... FOR UPDATE) by the
///     infrastructure layer for the duration of the DB transaction that
///     wraps every mutation, so only one in-flight mutation per wallet can
///     read-modify-write at a time.
///  2. The infrastructure mapping also enables Postgres's hidden `xmin`
///     system column as an EF Core concurrency token, providing a
///     defense-in-depth optimistic check in case a code path ever reads a
///     wallet outside the locking transaction.
/// </summary>
public sealed class Wallet : Entity
{
    public Guid CustomerId { get; private set; }
    public string Currency { get; private set; } = Money.DefaultCurrency;
    public long BalanceKobo { get; private set; }
    public long DailyOutboundLimitKobo { get; private set; }
    public WalletStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private Wallet()
    {
        // EF Core materialization
    }

    private Wallet(Guid id, Guid customerId, string currency, long dailyOutboundLimitKobo, DateTimeOffset createdAtUtc)
        : base(id)
    {
        CustomerId = customerId;
        Currency = currency;
        BalanceKobo = 0;
        DailyOutboundLimitKobo = dailyOutboundLimitKobo;
        Status = WalletStatus.Active;
        CreatedAtUtc = createdAtUtc;
    }

    public const long DefaultDailyOutboundLimitKobo = 50_000_000; // NGN 500,000.00

    public static Wallet Create(Guid customerId, DateTimeOffset nowUtc, string currency = "NGN",
        long dailyOutboundLimitKobo = DefaultDailyOutboundLimitKobo)
    {
        if (customerId == Guid.Empty)
            throw new InvalidAmountException("CustomerId is required to create a wallet.");

        return new Wallet(Guid.NewGuid(), customerId, currency, dailyOutboundLimitKobo, nowUtc);
    }

    public Money Balance => new(BalanceKobo, Currency);

    public void Credit(Money amount)
    {
        EnsureActive();
        EnsureCurrency(amount);
        if (amount.AmountKobo <= 0)
            throw new InvalidAmountException("Credit amount must be positive.");

        checked
        {
            BalanceKobo += amount.AmountKobo;
        }
    }

    /// <summary>
    /// Debits the wallet after verifying both sufficient balance and that the
    /// wallet's rolling daily outbound limit (WAT calendar day) is not exceeded.
    /// <paramref name="alreadyDebitedTodayKobo"/> must be computed by the caller
    /// from the ledger, inside the same locked transaction, so it is consistent.
    /// </summary>
    public void Debit(Money amount, long alreadyDebitedTodayKobo)
    {
        EnsureActive();
        EnsureCurrency(amount);
        if (amount.AmountKobo <= 0)
            throw new InvalidAmountException("Debit amount must be positive.");

        if (amount.AmountKobo > BalanceKobo)
            throw new InsufficientFundsException(Id, amount.AmountKobo, BalanceKobo);

        var totalToday = alreadyDebitedTodayKobo + amount.AmountKobo;
        if (totalToday > DailyOutboundLimitKobo)
            throw new DailyLimitExceededException(Id, amount.AmountKobo, alreadyDebitedTodayKobo, DailyOutboundLimitKobo);

        checked
        {
            BalanceKobo -= amount.AmountKobo;
        }
    }

    private void EnsureActive()
    {
        if (Status != WalletStatus.Active)
            throw new InvalidAmountException($"Wallet {Id} is not active (status: {Status}).");
    }

    private void EnsureCurrency(Money amount)
    {
        if (amount.Currency != Currency)
            throw new CurrencyMismatchException(Currency, amount.Currency);
    }
}
