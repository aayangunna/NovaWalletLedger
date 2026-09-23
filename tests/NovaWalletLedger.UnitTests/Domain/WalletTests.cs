using FluentAssertions;
using NovaWalletLedger.Domain.Common;
using NovaWalletLedger.Domain.Wallets;
using Xunit;

namespace NovaWalletLedger.UnitTests.Domain;

public class WalletTests
{
    private static Wallet NewWallet(long dailyLimitKobo = Wallet.DefaultDailyOutboundLimitKobo) =>
        Wallet.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, dailyOutboundLimitKobo: dailyLimitKobo);

    [Fact]
    public void Create_starts_with_zero_balance()
    {
        var wallet = NewWallet();
        wallet.BalanceKobo.Should().Be(0);
        wallet.Status.Should().Be(WalletStatus.Active);
    }

    [Fact]
    public void Credit_increases_balance()
    {
        var wallet = NewWallet();
        wallet.Credit(new Money(50_000));
        wallet.BalanceKobo.Should().Be(50_000);
    }

    [Fact]
    public void Credit_rejects_zero_or_negative_amount()
    {
        var wallet = NewWallet();
        var act = () => wallet.Credit(new Money(0));
        act.Should().Throw<InvalidAmountException>();
    }

    [Fact]
    public void Debit_reduces_balance_when_sufficient_funds()
    {
        var wallet = NewWallet();
        wallet.Credit(new Money(10_000));

        wallet.Debit(new Money(4_000), alreadyDebitedTodayKobo: 0);

        wallet.BalanceKobo.Should().Be(6_000);
    }

    [Fact]
    public void Debit_throws_when_balance_insufficient()
    {
        var wallet = NewWallet();
        wallet.Credit(new Money(1_000));

        var act = () => wallet.Debit(new Money(1_001), alreadyDebitedTodayKobo: 0);

        act.Should().Throw<InsufficientFundsException>();
        wallet.BalanceKobo.Should().Be(1_000, "a failed debit must not mutate the balance");
    }

    [Fact]
    public void Debit_never_allows_balance_to_go_negative_even_on_exact_boundary()
    {
        var wallet = NewWallet();
        wallet.Credit(new Money(500));

        wallet.Debit(new Money(500), alreadyDebitedTodayKobo: 0);

        wallet.BalanceKobo.Should().Be(0);
        var act = () => wallet.Debit(new Money(1), alreadyDebitedTodayKobo: 500);
        act.Should().Throw<InsufficientFundsException>();
    }

    [Fact]
    public void Debit_throws_when_daily_limit_would_be_exceeded_even_with_sufficient_balance()
    {
        var wallet = NewWallet(dailyLimitKobo: 1_000);
        wallet.Credit(new Money(10_000));

        var act = () => wallet.Debit(new Money(1_001), alreadyDebitedTodayKobo: 0);

        act.Should().Throw<DailyLimitExceededException>();
        wallet.BalanceKobo.Should().Be(10_000);
    }

    [Fact]
    public void Debit_accounts_for_amounts_already_debited_today()
    {
        var wallet = NewWallet(dailyLimitKobo: 1_000);
        wallet.Credit(new Money(10_000));

        wallet.Debit(new Money(600), alreadyDebitedTodayKobo: 0);

        var act = () => wallet.Debit(new Money(500), alreadyDebitedTodayKobo: 600);
        act.Should().Throw<DailyLimitExceededException>();
    }

    [Fact]
    public void Credit_and_debit_reject_currency_mismatch()
    {
        var wallet = NewWallet();
        var act = () => wallet.Credit(new Money(100, "USD"));
        act.Should().Throw<CurrencyMismatchException>();
    }
}
