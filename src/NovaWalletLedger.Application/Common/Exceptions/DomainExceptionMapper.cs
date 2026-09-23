using NovaWalletLedger.Domain.Common;

namespace NovaWalletLedger.Application.Common.Exceptions;

/// <summary>
/// Maps a domain rule violation to an HTTP status code and title, shared by
/// both the idempotency-replay path (which must persist the exact response
/// a failed request produced) and the global exception-handling middleware
/// in the API layer.
/// </summary>
public static class DomainExceptionMapper
{
    public static (int StatusCode, string Title) Map(DomainException ex) => ex switch
    {
        InsufficientFundsException => (422, "Insufficient funds"),
        DailyLimitExceededException => (422, "Daily transfer limit exceeded"),
        SameWalletTransferException => (400, "Invalid transfer"),
        CurrencyMismatchException => (400, "Currency mismatch"),
        InvalidAmountException => (400, "Invalid amount"),
        _ => (400, "Domain rule violation")
    };
}
