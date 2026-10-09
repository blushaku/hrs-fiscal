using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Server.Data;

namespace Hrs.Fiscal.Server.Flip;

public sealed record ValidationOptions(bool CheckTaxNumber, decimal Tolerance);

/// <summary>
/// Checks an OPERA folio before it is fiscalized: that it belongs to this property, and that OPERA's own numbers
/// add up (price × quantity, VAT per line, VAT per rate, folio total, payments). Any finding refuses the folio:
/// a receipt is only issued when the data is consistent.
/// </summary>
public static class FolioValidator
{
    /// <summary>Checks that need only the folio and the business.</summary>
    public static List<string> Check(OperaFolio f, BusinessInfo b, ValidationOptions o)
    {
        var errors = new List<string>();
        var tol = o.Tolerance;

        // ---- property ------------------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(b.OperaHotelCode))
            errors.Add("The OPERA property code is not set under Settings › Business & unit, so the folio's property cannot be checked.");
        else
        {
            foreach (var (code, field) in new[] { (f.HotelCode, "DocumentInfo.HotelCode"), (f.HotelInfoCode, "HotelInfo.HotelCode") })
                if (!string.IsNullOrEmpty(code) && !string.Equals(code.Trim(), b.OperaHotelCode.Trim(), StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Folio is for OPERA property {code} ({field}), but this server fiscalizes {b.OperaHotelCode}.");
            if (string.IsNullOrEmpty(f.HotelCode) && string.IsNullOrEmpty(f.HotelInfoCode))
                errors.Add("The folio does not name its OPERA property (HotelCode).");
        }
        if (o.CheckTaxNumber && !string.IsNullOrWhiteSpace(f.PropertyTaxNumber))
        {
            var t = f.PropertyTaxNumber.Trim();
            var known = new[] { b.Nui.ToString(), b.VatNo, b.FiscalizationNo }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim());
            if (!known.Any(k => string.Equals(k, t, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"Folio tax number {t} (DocumentInfo.PropertyTaxNumber) does not match this business (NUI {b.Nui}{(string.IsNullOrEmpty(b.VatNo) ? "" : ", VAT no. " + b.VatNo)}).");
        }

        // ---- lines ---------------------------------------------------------------------------
        foreach (var l in f.Lines)
        {
            var name = $"{l.Description} ({l.TrxCode})";
            if (l.Quantity <= 0) errors.Add($"{name}: quantity {l.Quantity} is not positive.");
            if (l.TaxInclusive && l.UnitPrice is { } up && up != 0 && Math.Abs(up * l.Quantity - l.Gross) > tol)
                errors.Add($"{name}: price {up:0.00} × quantity {l.Quantity:0.###} = {up * l.Quantity:0.00}, but the line amount is {l.Gross:0.00}.");
            if (l.VatPercent < 0 || l.VatPercent >= 100) errors.Add($"{name}: VAT rate {l.VatPercent:0.##}% is not possible.");
            if (l.Tax is { } tax)
            {
                var expected = l.Gross - l.Gross / (1 + l.VatPercent / 100m);
                if (Math.Abs(tax - expected) > tol)
                    errors.Add($"{name}: VAT {tax:0.00} at {l.VatPercent:0.##}% should be {expected:0.00} on {l.Gross:0.00}.");
                if (l.Net is { } net && l.TaxInclusive && Math.Abs(net + tax - l.Gross) > tol)
                    errors.Add($"{name}: net {net:0.00} + VAT {tax:0.00} does not equal the line amount {l.Gross:0.00}.");
            }
            else if (l.VatPercent == 0 && l.Net is { } net0 && l.TaxInclusive && Math.Abs(net0 - l.Gross) > tol)
                errors.Add($"{name}: no VAT posted, but net {net0:0.00} differs from the line amount {l.Gross:0.00}.");
        }

        // ---- folio totals ----------------------------------------------------------------------
        var gross = f.Lines.Sum(l => l.Gross);
        var lineTolerance = tol * Math.Max(1, f.Lines.Count);
        if (f.TotalGross is { } tg && Math.Abs(tg - gross) > lineTolerance)
            errors.Add($"The lines add up to {gross:0.00}, but OPERA's folio total is {tg:0.00}.");
        foreach (var t in f.TaxTotals)
        {
            var lines = f.Lines.Where(l => l.VatPercent == t.Percent).ToList();
            if (lines.Count == 0) { if (t.Tax != 0) errors.Add($"OPERA reports VAT {t.Percent:0.##}% of {t.Tax:0.00}, but no line has that rate."); continue; }
            var lineTax = lines.Sum(l => l.Tax ?? (l.Gross - l.Gross / (1 + l.VatPercent / 100m)));
            if (Math.Abs(lineTax - t.Tax) > tol * Math.Max(1, lines.Count))
                errors.Add($"VAT {t.Percent:0.##}%: the lines carry {lineTax:0.00}, but OPERA's total says {t.Tax:0.00}.");
            var groupGross = lines.Sum(l => l.Gross);
            var expected = groupGross - groupGross / (1 + t.Percent / 100m);
            if (Math.Abs(t.Tax - expected) > tol * Math.Max(1, lines.Count))
                errors.Add($"VAT {t.Percent:0.##}%: OPERA's total {t.Tax:0.00} should be {expected:0.00} on {groupGross:0.00}.");
        }
        if (Math.Abs(f.Paid - gross) > lineTolerance)
            errors.Add($"Payments {f.Paid:0.00} do not equal the charges {gross:0.00}: the folio is not balanced.");
        return errors;
    }

    /// <summary>After the receipt is built: its VAT per rate must match what OPERA calculated.</summary>
    public static List<string> CompareWithReceipt(OperaFolio f, PosCoupon c, IReadOnlyList<VatRateRow> rates, decimal tolerance)
    {
        var errors = new List<string>();
        var sign = c.Type == CouponType.Return ? -1m : 1m;
        foreach (var g in c.TaxGroups)
        {
            var pct = rates.FirstOrDefault(r => r.Letter == g.TaxRate)?.Percent;
            if (pct is null) continue;
            var lines = f.Lines.Where(l => l.VatPercent == pct).ToList();
            var operaTax = f.TaxTotals.FirstOrDefault(t => t.Percent == pct)?.Tax ?? lines.Sum(l => l.Tax ?? 0m);
            if (lines.Count == 0 || (operaTax == 0 && pct != 0)) continue;
            var receiptTax = g.TotalTax / 100m;
            if (Math.Abs(receiptTax - operaTax * sign) > tolerance * Math.Max(1, lines.Count))
                errors.Add($"VAT {g.TaxRate} ({pct:0.##}%): the receipt has {receiptTax:0.00}, OPERA calculated {operaTax * sign:0.00}.");
        }
        var receiptTotal = c.Total / 100m;
        var operaTotal = (f.TotalGross ?? f.Lines.Sum(l => l.Gross)) * sign;
        if (Math.Abs(receiptTotal - operaTotal) > tolerance * Math.Max(1, f.Lines.Count))
            errors.Add($"The receipt total {receiptTotal:0.00} differs from the folio total {operaTotal:0.00}.");
        return errors;
    }
}
