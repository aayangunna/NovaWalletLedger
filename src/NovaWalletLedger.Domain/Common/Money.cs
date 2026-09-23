namespace NovaWalletLedger.Domain.Common;

/// <summary>
/// Represents a monetary amount as an integer number of kobo (1 NGN = 100 kobo).
/// No floating-point type ever appears in the money path.
/// </summary>
public readonly record struct Money
{
    public long AmountKobo { get; }
    public string Currency { get; }

    public static readonly string DefaultCurrency = "NGN";

    public Money(long amountKobo, string currency = "NGN")
    {
        if (amountKobo < 0)
            throw new ArgumentOutOfRangeException(nameof(amountKobo), "Money amount cannot be negative.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a 3-letter ISO code.", nameof(currency));

        AmountKobo = amountKobo;
        Currency = currency.ToUpperInvariant();
    }

    public static Money Zero(string currency = "NGN") => new(0, currency);

    public static Money FromNaira(decimal naira, string currency = "NGN")
    {
        // Conversion boundary only — used at API edges when accepting Naira input.
        // Internally everything downstream is integer kobo.
        checked
        {
            var kobo = (long)Math.Round(naira * 100m, MidpointRounding.ToEven);
            return new Money(kobo, currency);
        }
    }

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        checked
        {
            return new Money(AmountKobo + other.AmountKobo, Currency);
        }
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        checked
        {
            var result = AmountKobo - other.AmountKobo;
            if (result < 0)
                throw new InvalidOperationException("Resulting amount cannot be negative.");
            return new Money(result, Currency);
        }
    }

    public bool IsGreaterThanOrEqualTo(Money other)
    {
        EnsureSameCurrency(other);
        return AmountKobo >= other.AmountKobo;
    }

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
            throw new InvalidOperationException($"Currency mismatch: {Currency} vs {other.Currency}.");
    }

    public decimal ToNaira() => AmountKobo / 100m;

    public override string ToString() => $"{Currency} {ToNaira():N2} ({AmountKobo} kobo)";
}
