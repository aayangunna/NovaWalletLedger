namespace NovaWalletLedger.Domain.Common;

/// <summary>
/// Base type for all domain-rule violations. The API layer maps these to
/// RFC 7807 ProblemDetails responses with an appropriate HTTP status code.
/// </summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message) : base(message)
    {
    }
}

public sealed class InsufficientFundsException : DomainException
{
    public InsufficientFundsException(Guid walletId, long requestedKobo, long availableKobo)
        : base($"Wallet {walletId} has insufficient funds. Requested {requestedKobo} kobo, available {availableKobo} kobo.")
    {
        WalletId = walletId;
        RequestedKobo = requestedKobo;
        AvailableKobo = availableKobo;
    }

    public Guid WalletId { get; }
    public long RequestedKobo { get; }
    public long AvailableKobo { get; }
}

public sealed class DailyLimitExceededException : DomainException
{
    public DailyLimitExceededException(Guid walletId, long requestedKobo, long alreadyUsedKobo, long limitKobo)
        : base($"Wallet {walletId} would exceed its daily outbound transfer limit of {limitKobo} kobo. " +
               $"Already transferred {alreadyUsedKobo} kobo today, requested additional {requestedKobo} kobo.")
    {
        WalletId = walletId;
        RequestedKobo = requestedKobo;
        AlreadyUsedKobo = alreadyUsedKobo;
        LimitKobo = limitKobo;
    }

    public Guid WalletId { get; }
    public long RequestedKobo { get; }
    public long AlreadyUsedKobo { get; }
    public long LimitKobo { get; }
}

public sealed class InvalidAmountException : DomainException
{
    public InvalidAmountException(string message) : base(message)
    {
    }
}

public sealed class SameWalletTransferException : DomainException
{
    public SameWalletTransferException(Guid walletId)
        : base($"Cannot transfer from wallet {walletId} to itself.")
    {
        WalletId = walletId;
    }

    public Guid WalletId { get; }
}

public sealed class CurrencyMismatchException : DomainException
{
    public CurrencyMismatchException(string expected, string actual)
        : base($"Currency mismatch: expected {expected}, got {actual}.")
    {
    }
}
