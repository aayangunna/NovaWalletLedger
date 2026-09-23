using FluentAssertions;
using NovaWalletLedger.Domain.Common;
using Xunit;

namespace NovaWalletLedger.UnitTests.Domain;

public class WestAfricaClockTests
{
    [Fact]
    public void ToWat_applies_fixed_utc_plus_one_offset()
    {
        var utc = new DateTimeOffset(2026, 1, 1, 23, 30, 0, TimeSpan.Zero);
        var wat = WestAfricaClock.ToWat(utc);
        wat.Hour.Should().Be(0);
        wat.Day.Should().Be(2);
    }

    [Fact]
    public void StartOfWatDayUtc_resets_at_23_00_utc_the_previous_day()
    {
        // 23:30 UTC on Jan 1 is already 00:30 WAT on Jan 2, so the WAT
        // calendar day boundary in UTC is 23:00 the previous UTC day.
        var utc = new DateTimeOffset(2026, 1, 1, 23, 30, 0, TimeSpan.Zero);
        var startOfDay = WestAfricaClock.StartOfWatDayUtc(utc);
        startOfDay.Should().Be(new DateTimeOffset(2026, 1, 1, 23, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void WatDate_just_before_midnight_wat_is_previous_day()
    {
        var utc = new DateTimeOffset(2026, 1, 1, 22, 59, 0, TimeSpan.Zero); // 23:59 WAT Jan 1
        WestAfricaClock.WatDate(utc).Should().Be(new DateOnly(2026, 1, 1));
    }
}
