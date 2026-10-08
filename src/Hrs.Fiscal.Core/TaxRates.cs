namespace Hrs.Fiscal.Core;

/// <summary>
/// Kosovo VAT rate letters used on fiscal coupons.
/// A = exempt, C = 0%, D = 8% (reduced), E = 18% (standard).
/// Percentages are configurable in case the law changes; the letters are fixed by ATK.
/// </summary>
public sealed class TaxRateTable
{
    private readonly Dictionary<string, decimal> _rates;

    public TaxRateTable(IDictionary<string, decimal>? rates = null)
    {
        _rates = rates is null ? new Dictionary<string, decimal>(Default, StringComparer.Ordinal) : new Dictionary<string, decimal>(rates, StringComparer.Ordinal);
    }

    public static IReadOnlyDictionary<string, decimal> Default { get; } = new Dictionary<string, decimal>
    {
        ["A"] = 0m,
        ["C"] = 0m,
        ["D"] = 8m,
        ["E"] = 18m,
    };

    public bool IsKnown(string letter) => _rates.ContainsKey(letter);

    public decimal PercentFor(string letter) =>
        _rates.TryGetValue(letter, out var p)
            ? p
            : throw new FiscalValidationException($"Unknown tax rate letter '{letter}'. Allowed: {string.Join(", ", _rates.Keys)}");

    public IEnumerable<string> Letters => _rates.Keys.OrderBy(k => k, StringComparer.Ordinal);
}

/// <summary>How VAT is extracted from a VAT-inclusive gross amount per tax group.</summary>
public enum VatRounding
{
    /// <summary>tax = round(gross * r / (100 + r)) half away from zero; net = gross - tax. Default.</summary>
    RoundTaxHalfUp,

    /// <summary>net = floor(gross * 100 / (100 + r)); tax = gross - net. Reproduces the numbers in ATK's sample code.</summary>
    TruncateNet,
}
