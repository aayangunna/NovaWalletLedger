using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using NovaWalletLedger.Api.Contracts;
using NovaWalletLedger.Application.Features.Wallets.CreateWallet;
using NovaWalletLedger.Application.Features.Wallets.CreditWallet;
using NovaWalletLedger.Application.Features.Wallets.GetBalance;
using NovaWalletLedger.Application.Features.Wallets.Transfer;
using NovaWalletLedger.IntegrationTests.Infrastructure;
using Xunit;

namespace NovaWalletLedger.IntegrationTests.Wallets;

/// <summary>
/// The hard constraint under test: "concurrent transfer requests against the
/// same wallet must never allow the balance to go negative or allow a
/// double-spend." These tests fire many transfer requests at the *same*
/// source wallet in parallel, against the real API host and a real Postgres
/// instance (via Testcontainers) — not a mocked or in-memory store — so the
/// SELECT ... FOR UPDATE row locking in WalletLockService is genuinely
/// exercised under contention.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class ConcurrencyTests
{
    private readonly NovaWalletApiFactory _factory;

    public ConcurrencyTests(NovaWalletApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Concurrent_transfers_that_would_overdraw_the_wallet_never_push_the_balance_negative()
    {
        var sourceClient = _factory.CreateClient();
        var sourceCustomerId = Guid.NewGuid();
        await sourceClient.AuthenticateAsync(sourceCustomerId);

        var createSourceResponse = await sourceClient.PostAsJsonAsync("/api/wallets/", new { });
        var sourceWallet = await createSourceResponse.Content.ReadFromJsonAsync<CreateWalletResponse>();

        var destinationClient = _factory.CreateClient();
        await destinationClient.AuthenticateAsync(Guid.NewGuid());
        var createDestinationResponse = await destinationClient.PostAsJsonAsync("/api/wallets/", new { });
        var destinationWallet = await createDestinationResponse.Content.ReadFromJsonAsync<CreateWalletResponse>();

        const long startingBalanceKobo = 10_000_00; // NGN 10,000.00
        const long amountPerTransferKobo = 300_00;  // NGN 300.00
        const int concurrentRequests = 50;          // 50 x 300 = 15,000 > 10,000 available

        await sourceClient.PostAsJsonAsync(
            $"/api/wallets/{sourceWallet!.WalletId}/credit", new CreditWalletRequest(startingBalanceKobo, null, null));

        var transferRequest = new TransferRequest(destinationWallet!.WalletId, amountPerTransferKobo, "Concurrency test");

        var tasks = Enumerable.Range(0, concurrentRequests).Select(async _ =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/wallets/{sourceWallet.WalletId}/transfer")
            {
                Content = JsonContent.Create(transferRequest)
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            request.Headers.Authorization = sourceClient.DefaultRequestHeaders.Authorization;

            return await sourceClient.SendAsync(request);
        });

        var responses = await Task.WhenAll(tasks);

        var successCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var rejectedCount = responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);

        (successCount + rejectedCount).Should().Be(concurrentRequests, "every request must resolve to either success or a clean domain rejection — never an unhandled error");
        responses.Should().OnlyContain(
            r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.UnprocessableEntity);

        // At 300/transfer against a 10,000 balance, at most 33 can succeed.
        successCount.Should().BeLessThanOrEqualTo((int)(startingBalanceKobo / amountPerTransferKobo));

        var sourceBalanceResponse = await sourceClient.GetAsync($"/api/wallets/{sourceWallet.WalletId}/balance");
        var sourceBalance = await sourceBalanceResponse.Content.ReadFromJsonAsync<GetBalanceResponse>();

        sourceBalance!.BalanceKobo.Should().BeGreaterThanOrEqualTo(0, "the balance must never go negative under concurrent load");
        sourceBalance.BalanceKobo.Should().Be(
            startingBalanceKobo - successCount * amountPerTransferKobo,
            "the ending balance must exactly match starting balance minus every transfer that actually succeeded — no lost or duplicated debits");

        var destinationBalanceResponse = await destinationClient.GetAsync($"/api/wallets/{destinationWallet.WalletId}/balance");
        var destinationBalance = await destinationBalanceResponse.Content.ReadFromJsonAsync<GetBalanceResponse>();

        destinationBalance!.BalanceKobo.Should().Be(
            successCount * amountPerTransferKobo,
            "the destination must be credited exactly once per successful transfer — no double-crediting either");
    }

    [Fact]
    public async Task Concurrently_replaying_the_same_idempotency_key_only_applies_the_transfer_once()
    {
        var sourceClient = _factory.CreateClient();
        await sourceClient.AuthenticateAsync(Guid.NewGuid());
        var createSourceResponse = await sourceClient.PostAsJsonAsync("/api/wallets/", new { });
        var sourceWallet = await createSourceResponse.Content.ReadFromJsonAsync<CreateWalletResponse>();

        var destinationClient = _factory.CreateClient();
        await destinationClient.AuthenticateAsync(Guid.NewGuid());
        var createDestinationResponse = await destinationClient.PostAsJsonAsync("/api/wallets/", new { });
        var destinationWallet = await createDestinationResponse.Content.ReadFromJsonAsync<CreateWalletResponse>();

        const long startingBalanceKobo = 5_000_00;
        const long transferAmountKobo = 1_000_00;

        await sourceClient.PostAsJsonAsync(
            $"/api/wallets/{sourceWallet!.WalletId}/credit", new CreditWalletRequest(startingBalanceKobo, null, null));

        var sharedIdempotencyKey = Guid.NewGuid().ToString();
        var transferRequest = new TransferRequest(destinationWallet!.WalletId, transferAmountKobo, "Race test");

        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/wallets/{sourceWallet.WalletId}/transfer")
            {
                Content = JsonContent.Create(transferRequest)
            };
            request.Headers.Add("Idempotency-Key", sharedIdempotencyKey);
            request.Headers.Authorization = sourceClient.DefaultRequestHeaders.Authorization;

            return await sourceClient.SendAsync(request);
        });

        var responses = await Task.WhenAll(tasks);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK,
            "every replay of the same key+payload must succeed and return the same result, never fail");

        var transferIds = new HashSet<Guid>();
        foreach (var response in responses)
        {
            var body = await response.Content.ReadFromJsonAsync<TransferResponse>();
            transferIds.Add(body!.TransferId);
        }

        transferIds.Should().ContainSingle("all 20 concurrent replays of the same Idempotency-Key must resolve to the exact same transfer");

        var sourceBalanceResponse = await sourceClient.GetAsync($"/api/wallets/{sourceWallet.WalletId}/balance");
        var sourceBalance = await sourceBalanceResponse.Content.ReadFromJsonAsync<GetBalanceResponse>();

        sourceBalance!.BalanceKobo.Should().Be(
            startingBalanceKobo - transferAmountKobo,
            "the debit must have been applied exactly once despite 20 concurrent requests sharing one Idempotency-Key");
    }
}
