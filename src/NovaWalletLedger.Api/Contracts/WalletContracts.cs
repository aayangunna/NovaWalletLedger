namespace NovaWalletLedger.Api.Contracts;

public sealed record CreditWalletRequest(long AmountKobo, string? ExternalReference, string? Description);

public sealed record TransferRequest(Guid DestinationWalletId, long AmountKobo, string? Description);

public sealed record IssueTokenRequest(Guid CustomerId);

public sealed record IssueTokenResponse(string AccessToken, string TokenType, int ExpiresInSeconds);
