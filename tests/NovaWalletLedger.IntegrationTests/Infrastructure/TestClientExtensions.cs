using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NovaWalletLedger.IntegrationTests.Infrastructure;

internal sealed record IssueTokenRequest(Guid CustomerId);
internal sealed record IssueTokenResponse(string AccessToken, string TokenType, int ExpiresInSeconds);

public static class TestClientExtensions
{
    public static async Task AuthenticateAsync(this HttpClient client, Guid customerId)
    {
        var response = await client.PostAsJsonAsync("/api/auth/token", new IssueTokenRequest(customerId));
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<IssueTokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
    }
}
