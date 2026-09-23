using NovaWalletLedger.Api.Contracts;
using NovaWalletLedger.Infrastructure.Auth;

namespace NovaWalletLedger.Api.Endpoints.Auth;

/// <summary>
/// Mock identity issuer. FirstBank's real system would delegate this to an
/// OIDC provider; this endpoint exists purely so the API is runnable and
/// testable end-to-end without one. It is intentionally unauthenticated —
/// it is the front door, not a protected resource — and issues a token for
/// whatever customerId the caller asks for. This is clearly documented
/// as non-production in the README.
/// </summary>
public static class TokenEndpoint
{
    public static IEndpointRouteBuilder MapTokenEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/token", (IssueTokenRequest request, IJwtTokenService tokenService) =>
            {
                var token = tokenService.IssueToken(request.CustomerId);
                return Results.Ok(new IssueTokenResponse(token, "Bearer", 3600));
            })
            .WithName("IssueMockToken")
            .WithTags("Auth")
            .WithSummary("Mock JWT issuer for testing (not a production auth server)")
            .Produces<IssueTokenResponse>()
            .AllowAnonymous();

        return app;
    }
}
