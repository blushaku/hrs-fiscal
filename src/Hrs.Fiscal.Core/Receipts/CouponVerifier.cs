using Hrs.Fiscal.Core.Atk;

namespace Hrs.Fiscal.Core.Receipts;

/// <summary>
/// Independent arithmetic check of a finished PosCoupon, run before it is signed: every line, every VAT group and the
/// totals must add up, and the payments must equal the total. Catches errors in mapping or rounding code, so a wrong
/// receipt is never signed or sent to ATK.
/// </summary>
public static class CouponVerifier
{
    public static IReadOnlyList<string> Verify(PosCoupon c, TaxRateTable rates)
    {
        var errors = new List<string>();
        var grossByLetter = new Dictionary<string, long>(StringComparer.Ordinal); // €0.0001

        foreach (var item in c.Items)
        {
            if (!rates.IsKnown(item.TaxRate)) { errors.Add($"Line '{item.Name}': VAT letter {item.TaxRate} is not configured."); continue; }
            if (item.Quantity <= 0) errors.Add($"Line '{item.Name}': quantity must be positive.");
            if (item.Price < 0 || item.Total < 0) errors.Add($"Line '{item.Name}': negative amount.");
            var expected = (long)Math.Round((decimal)item.Price * (decimal)item.Quantity, 0, MidpointRounding.AwayFromZero);
            // Total may be lower than price × quantity by a discount, never higher.
            if (item.Total > expected + 1) errors.Add($"Line '{item.Name}': total {Money.PriceUnitsToCents(item.Total) / 100m:0.00} is more than price × quantity.");
            grossByLetter[item.TaxRate] = grossByLetter.GetValueOrDefault(item.TaxRate) + item.Total;
        }

        long total = 0, tax = 0;
        foreach (var g in c.TaxGroups)
        {
            var gross = g.TotalForTax + g.TotalTax;
            if (!grossByLetter.TryGetValue(g.TaxRate, out var linesUnits))
            {
                errors.Add($"VAT group {g.TaxRate} has no lines.");
                continue;
            }
            if (Money.PriceUnitsToCents(linesUnits) != gross)
                errors.Add($"VAT group {g.TaxRate}: lines add up to {Money.PriceUnitsToCents(linesUnits) / 100m:0.00}, group says {gross / 100m:0.00}.");
            if (rates.IsKnown(g.TaxRate))
            {
                var pct = rates.PercentFor(g.TaxRate);
                var expectedTax = gross * pct / (100 + pct);
                if (Math.Abs(g.TotalTax - expectedTax) > 1m)
                    errors.Add($"VAT group {g.TaxRate} ({pct:0.##}%): VAT {g.TotalTax / 100m:0.00} does not match {expectedTax / 100m:0.00} expected on {gross / 100m:0.00}.");
            }
            if (g.TotalTax < 0 || g.TotalForTax < 0) errors.Add($"VAT group {g.TaxRate}: negative amount.");
            total += gross;
            tax += g.TotalTax;
        }
        foreach (var letter in grossByLetter.Keys.Where(l => c.TaxGroups.All(g => g.TaxRate != l)))
            errors.Add($"Lines with VAT {letter} have no VAT group.");

        if (c.Total != total) errors.Add($"Receipt total {c.Total / 100m:0.00} differs from the VAT groups ({total / 100m:0.00}).");
        if (c.TotalTax != tax) errors.Add($"Receipt VAT {c.TotalTax / 100m:0.00} differs from the VAT groups ({tax / 100m:0.00}).");
        if (c.TotalNoTax != c.Total - c.TotalTax) errors.Add("Receipt net amount is not total minus VAT.");
        var paid = c.Payments.Sum(p => p.Amount);
        if (paid != c.Total) errors.Add($"Payments {paid / 100m:0.00} do not equal the receipt total {c.Total / 100m:0.00}.");
        if (c.Payments.Any(p => p.Amount <= 0)) errors.Add("A payment amount is zero or negative.");
        if (c.Type == CouponType.Return && c.ReferenceNo == 0) errors.Add("A return must refer to the original receipt.");
        return errors;
    }
}
