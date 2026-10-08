namespace Hrs.Fiscal.Core;

/// <summary>
/// ATK monetary conventions (see github.com/fiskalizimi/pos-csharp "Important Notes"):
/// - item prices and item line totals are integers in €0.0001 units (€1.00 = 10000)
/// - coupon totals, tax groups and payments are integers in €0.01 units (€1.00 = 100)
/// </summary>
public static class Money
{
    public const long PriceUnitsPerEuro = 10_000;
    public const long CentsPerEuro = 100;

    public static long ToPriceUnits(decimal euros) =>
        (long)decimal.Round(euros * PriceUnitsPerEuro, 0, MidpointRounding.AwayFromZero);

    public static long ToCents(decimal euros) =>
        (long)decimal.Round(euros * CentsPerEuro, 0, MidpointRounding.AwayFromZero);

    /// <summary>Converts €0.0001 units to cents, rounding half away from zero.</summary>
    public static long PriceUnitsToCents(long priceUnits) =>
        (long)decimal.Round(priceUnits / 100m, 0, MidpointRounding.AwayFromZero);

    public static decimal CentsToEuros(long cents) => cents / (decimal)CentsPerEuro;
    public static decimal PriceUnitsToEuros(long units) => units / (decimal)PriceUnitsPerEuro;
}
