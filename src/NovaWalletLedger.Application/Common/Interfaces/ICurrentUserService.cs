namespace NovaWalletLedger.Application.Common.Interfaces;

/// <summary>Identity of the authenticated caller, derived from JWT claims.</summary>
public interface ICurrentUserService
{
    Guid CustomerId { get; }
    string? CorrelationId { get; }
}
