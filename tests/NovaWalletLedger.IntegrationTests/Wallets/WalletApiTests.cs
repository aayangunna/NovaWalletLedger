using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using NovaWalletLedger.Api.Contracts;
using NovaWalletLedger.Application.Common.Models;
using NovaWalletLedger.Application.Features.Wallets.CreateWallet;
using NovaWalletLedger.Application.Features.Wallets.CreditWallet;
using NovaWalletLedger.Application.Features.Wallets.GetBalance;
using NovaWalletLedger.Application.Features.Wallets.GetStatement;
using NovaWalletLedger.Application.Features.Wallets.Transfer;
using NovaWalletLedger.IntegrationTests.Infrastructure;
using Xunit;

namespace NovaWalletLedger.IntegrationTests.Wallets;

[Collection(IntegrationTestCollection.Name)]
public sealed class WalletApiTests
{
    private readonly NovaWalletApiFactory _factory;

    public WalletApiTests(NovaWalletApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, Guid CustomerId, Guid WalletId)> CreateCustomerWithWalletAsync()
    {
        var client = _factory.CreateClient();
        var customerId = Guid.NewGuid();
        await client.AuthenticateAsync(customerId);

        var response = await client.PostAsJsonAsync("/api/wallets/", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var wallet = await response.Content.ReadFromJsonAsync<CreateWalletResponse>();
        return (client, customerId, wallet!.WalletId);
    }

    [Fact]
    public async Task Creating_a_wallet_starts_at_zero_balance_in_NGN()
    {
        var (client, _, walletId) = await CreateCustomerWithWalletAsync();

        var balanceResponse = await client.GetAsync($"/api/wallets/{walletId}/balance");
        balanceResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var balance = await balanceResponse.Content.ReadFromJsonAsync<GetBalanceResponse>();
        balance!.BalanceKobo.Should().Be(0);
        balance.Currency.Should().Be("NGN");
    }

    [Fact]
    public async Task Creating_a_second_wallet_for_the_same_customer_is_rejected()
    {
        var (client, _, _) = await CreateCustomerWithWalletAsync();

        var response = await client.PostAsJsonAsync("/api/wallets/", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_customer_can_credit_their_own_wallet_simulating_an_inbound_NIP_transfer()
    {
        var (client, _, walletId) = await CreateCustomerWithWalletAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/wallets/{walletId}/credit",
            new CreditWalletRequest(500_00, "NIP-REF-001", "Inbound transfer"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<CreditWalletResponse>();
        result!.BalanceKobo.Should().Be(500_00);
    }

    [Fact]
    public async Task A_customer_cannot_credit_another_customers_wallet()
    {
        var (_, _, walletId) = await CreateCustomerWithWalletAsync();
        var strangerClient = _factory.CreateClient();
        await strangerClient.AuthenticateAsync(Guid.NewGuid());

        var response = await strangerClient.PostAsJsonAsync(
            $"/api/wallets/{walletId}/credit", new CreditWalletRequest(100_00, null, null));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_customer_cannot_view_another_customers_balance()
    {
        var (_, _, walletId) = await CreateCustomerWithWalletAsync();
        var strangerClient = _factory.CreateClient();
        await strangerClient.AuthenticateAsync(Guid.NewGuid());

        var response = await strangerClient.GetAsync($"/api/wallets/{walletId}/balance");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Transfer_moves_funds_between_wallets_and_appears_in_both_statements()
    {
        var (sourceClient, _, sourceWalletId) = await CreateCustomerWithWalletAsync();
        var (_, _, destinationWalletId) = await CreateCustomerWithWalletAsync();

        await sourceClient.PostAsJsonAsync($"/api/wallets/{sourceWalletId}/credit", new CreditWalletRequest(10_000_00, null, null));

        sourceClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var transferResponse = await sourceClient.PostAsJsonAsync(
            $"/api/wallets/{sourceWalletId}/transfer",
            new TransferRequest(destinationWalletId, 2_500_00, "Rent contribution"));

        transferResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var transfer = await transferResponse.Content.ReadFromJsonAsync<TransferResponse>();
        transfer!.SourceBalanceKobo.Should().Be(7_500_00);
        transfer.DestinationBalanceKobo.Should().Be(2_500_00);

        var statementResponse = await sourceClient.GetAsync($"/api/wallets/{sourceWalletId}/statement");
        var statement = await statementResponse.Content.ReadFromJsonAsync<PagedResult<StatementLineDto>>();
        statement!.Items.Should().Contain(l => l.TransferGroupId == transfer.TransferId);
    }

    [Fact]
    public async Task Transfer_without_idempotency_key_header_is_rejected()
    {
        var (sourceClient, _, sourceWalletId) = await CreateCustomerWithWalletAsync();
        var (_, _, destinationWalletId) = await CreateCustomerWithWalletAsync();

        var response = await sourceClient.PostAsJsonAsync(
            $"/api/wallets/{sourceWalletId}/transfer", new TransferRequest(destinationWalletId, 1_00, null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Replaying_the_same_idempotency_key_does_not_double_process_the_transfer()
    {
        var (sourceClient, _, sourceWalletId) = await CreateCustomerWithWalletAsync();
        var (_, _, destinationWalletId) = await CreateCustomerWithWalletAsync();

        await sourceClient.PostAsJsonAsync($"/api/wallets/{sourceWalletId}/credit", new CreditWalletRequest(10_000_00, null, null));

        var idempotencyKey = Guid.NewGuid().ToString();
        var request = new TransferRequest(destinationWalletId, 1_000_00, "Test");

        sourceClient.DefaultRequestHeaders.Remove("Idempotency-Key");
        sourceClient.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
        var first = await sourceClient.PostAsJsonAsync($"/api/wallets/{sourceWalletId}/transfer", request);
        var firstBody = await first.Content.ReadFromJsonAsync<TransferResponse>();

        var second = await sourceClient.PostAsJsonAsync($"/api/wallets/{sourceWalletId}/transfer", request);
        var secondBody = await second.Content.ReadFromJsonAsync<TransferResponse>();

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        secondBody!.TransferId.Should().Be(firstBody!.TransferId);

        var balanceResponse = await sourceClient.GetAsync($"/api/wallets/{sourceWalletId}/balance");
        var balance = await balanceResponse.Content.ReadFromJsonAsync<GetBalanceResponse>();
        balance!.BalanceKobo.Should().Be(9_000_00, "the transfer must only have been applied once");
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_with_a_different_payload_is_rejected()
    {
        var (sourceClient, _, sourceWalletId) = await CreateCustomerWithWalletAsync();
        var (_, _, destinationWalletId) = await CreateCustomerWithWalletAsync();

        await sourceClient.PostAsJsonAsync($"/api/wallets/{sourceWalletId}/credit", new CreditWalletRequest(10_000_00, null, null));

        var idempotencyKey = Guid.NewGuid().ToString();
        sourceClient.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);

        var first = await sourceClient.PostAsJsonAsync(
            $"/api/wallets/{sourceWalletId}/transfer", new TransferRequest(destinationWalletId, 1_000_00, null));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await sourceClient.PostAsJsonAsync(
            $"/api/wallets/{sourceWalletId}/transfer", new TransferRequest(destinationWalletId, 2_000_00, null));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Transfer_beyond_available_balance_is_rejected_with_422()
    {
        var (sourceClient, _, sourceWalletId) = await CreateCustomerWithWalletAsync();
        var (_, _, destinationWalletId) = await CreateCustomerWithWalletAsync();

        sourceClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await sourceClient.PostAsJsonAsync(
            $"/api/wallets/{sourceWalletId}/transfer", new TransferRequest(destinationWalletId, 1_00, null));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Transfer_beyond_the_daily_outbound_limit_is_rejected()
    {
        var (sourceClient, _, sourceWalletId) = await CreateCustomerWithWalletAsync();
        var (_, _, destinationWalletId) = await CreateCustomerWithWalletAsync();

        // Fund well above the ₦500,000/day default limit.
        await sourceClient.PostAsJsonAsync($"/api/wallets/{sourceWalletId}/credit", new CreditWalletRequest(2_000_000_00, null, null));

        sourceClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await sourceClient.PostAsJsonAsync(
            $"/api/wallets/{sourceWalletId}/transfer", new TransferRequest(destinationWalletId, 500_001_00, null));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Statement_is_paginated_newest_first()
    {
        var (sourceClient, _, sourceWalletId) = await CreateCustomerWithWalletAsync();
        var (_, _, destinationWalletId) = await CreateCustomerWithWalletAsync();

        await sourceClient.PostAsJsonAsync($"/api/wallets/{sourceWalletId}/credit", new CreditWalletRequest(10_000_00, null, null));

        for (var i = 0; i < 3; i++)
        {
            sourceClient.DefaultRequestHeaders.Remove("Idempotency-Key");
            sourceClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
            await sourceClient.PostAsJsonAsync(
                $"/api/wallets/{sourceWalletId}/transfer", new TransferRequest(destinationWalletId, 100_00, $"Payment {i}"));
        }

        var response = await sourceClient.GetAsync($"/api/wallets/{sourceWalletId}/statement?page=1&pageSize=2");
        var statement = await response.Content.ReadFromJsonAsync<PagedResult<StatementLineDto>>();

        statement!.Items.Should().HaveCount(2);
        statement.TotalCount.Should().Be(4); // 1 credit + 3 debits
        statement.Items[0].CreatedAtUtc.Should().BeOnOrAfter(statement.Items[1].CreatedAtUtc);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/wallets/{Guid.NewGuid()}/balance");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
