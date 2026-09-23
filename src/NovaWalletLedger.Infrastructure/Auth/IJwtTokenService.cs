namespace NovaWalletLedger.Infrastructure.Auth;

public interface IJwtTokenService
{
    /// <summary>Issues a bearer token for the mock/test identity provider.</summary>
    string IssueToken(Guid customerId);
}
