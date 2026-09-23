using FluentAssertions;
using NovaWalletLedger.Domain.Common;
using Xunit;

namespace NovaWalletLedger.UnitTests.Domain;

public class MoneyTests
{
    [Fact]
    public void Constructor_rejects_negative_amount()
    {
        var act = () => new Money(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void FromNaira_converts_to_kobo_without_float_drift()
    {
        var money = Money.FromNaira(1234.56m);
        money.AmountKobo.Should().Be(123_456);
    }

    [Fact]
    public void FromNaira_handles_values_that_break_naive_float_conversion()
    {
        // 0.1 + 0.2 famously != 0.3 in binary floating point. Money must
        // never round-trip through double/float, only decimal -> long kobo.
        var money = Money.FromNaira(0.1m + 0.2m);
        money.AmountKobo.Should().Be(30);
    }

    [Fact]
    public void Add_sums_kobo_amounts_exactly()
    {
        var a = new Money(1_00);
        var b = new Money(2_50);
        (a.Add(b)).AmountKobo.Should().Be(3_50);
    }

    [Fact]
    public void Subtract_throws_when_result_would_be_negative()
    {
        var a = new Money(100);
        var b = new Money(200);
        var act = () => a.Subtract(b);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Add_throws_on_currency_mismatch()
    {
        var ngn = new Money(100, "NGN");
        var usd = new Money(100, "USD");
        var act = () => ngn.Add(usd);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Currency mismatch*");
    }

    [Fact]
    public void Add_throws_on_overflow_instead_of_wrapping_silently()
    {
        var a = new Money(long.MaxValue);
        var b = new Money(1);
        var act = () => a.Add(b);
        act.Should().Throw<OverflowException>();
    }
}
