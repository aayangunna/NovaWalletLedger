namespace NovaWalletLedger.Domain.Common;

/// <summary>
/// West Africa Time (WAT) is a fixed UTC+1 offset with no daylight-saving
/// rules, so a hardcoded offset is used instead of a named IANA/Windows time
/// zone (e.g. "Africa/Lagos"). This keeps the calculation correct and
/// deterministic across host OSes/containers regardless of whether the tzdata
/// package is installed, which matters because the daily outbound-transfer
/// limit resets at midnight WAT.
/// </summary>
public static class WestAfricaClock
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(1);

    public static DateTimeOffset ToWat(DateTimeOffset utc) => utc.ToOffset(Offset);

    public static DateOnly WatDate(DateTimeOffset utc) => DateOnly.FromDateTime(ToWat(utc).DateTime);

    public static DateTimeOffset StartOfWatDayUtc(DateOnly watDate) =>
        new DateTimeOffset(watDate.ToDateTime(TimeOnly.MinValue), Offset).ToUniversalTime();

    public static DateTimeOffset StartOfWatDayUtc(DateTimeOffset utc) => StartOfWatDayUtc(WatDate(utc));
}
