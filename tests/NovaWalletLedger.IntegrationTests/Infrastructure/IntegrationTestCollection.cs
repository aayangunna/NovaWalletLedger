using Xunit;

namespace NovaWalletLedger.IntegrationTests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class IntegrationTestCollection : ICollectionFixture<NovaWalletApiFactory>
{
    public const string Name = "NovaWallet API integration tests";
}
