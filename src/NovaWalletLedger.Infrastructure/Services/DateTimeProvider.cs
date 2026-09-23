using NovaWalletLedger.Application.Common.Interfaces;

namespace NovaWalletLedger.Infrastructure.Services;

public sealed class DateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
