namespace NovaWalletLedger.Infrastructure.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// Symmetric signing key for the mock issuer. In production this would
    /// be replaced by FirstBank's real identity provider (OIDC/JWKS) — this
    /// service only needs to validate bearer tokens and read claims, not
    /// issue them, so the mock issuer exists purely to make the API
    /// runnable/testable end-to-end without standing up a full auth server.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "novawallet-mock-issuer";
    public string Audience { get; set; } = "novawallet-ledger-api";
    public int AccessTokenLifetimeMinutes { get; set; } = 60;
}
