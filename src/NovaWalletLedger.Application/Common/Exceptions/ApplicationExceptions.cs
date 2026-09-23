namespace NovaWalletLedger.Application.Common.Exceptions;

public sealed class WalletNotFoundException : Exception
{
    public WalletNotFoundException(Guid walletId) : base($"Wallet {walletId} was not found.")
    {
        WalletId = walletId;
    }

    public Guid WalletId { get; }
}

public sealed class WalletAlreadyExistsException : Exception
{
    public WalletAlreadyExistsException(Guid customerId)
        : base($"Customer {customerId} already has a wallet.")
    {
        CustomerId = customerId;
    }

    public Guid CustomerId { get; }
}

public sealed class ForbiddenException : Exception
{
    public ForbiddenException(string message) : base(message)
    {
    }
}

public sealed class IdempotencyKeyRequiredException : Exception
{
    public IdempotencyKeyRequiredException() : base("The Idempotency-Key header is required for this operation.")
    {
    }
}

public sealed class IdempotencyKeyConflictException : Exception
{
    public IdempotencyKeyConflictException(string key)
        : base($"Idempotency-Key '{key}' was already used with a different request payload.")
    {
        Key = key;
    }

    public string Key { get; }
}

public sealed class IdempotentRequestInFlightException : Exception
{
    public IdempotentRequestInFlightException(string key)
        : base($"A request with Idempotency-Key '{key}' is already being processed.")
    {
        Key = key;
    }

    public string Key { get; }
}
