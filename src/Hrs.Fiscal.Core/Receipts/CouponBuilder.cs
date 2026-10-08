using Hrs.Fiscal.Core.Atk;

namespace Hrs.Fiscal.Core.Receipts;

/// <summary>
/// Builds and validates ATK PosCoupon / CitizenCoupon messages from a <see cref="ReceiptRequest"/>.
/// - identical items (name, unit, price, rate, category) are merged into one line (TR Art 25)
/// - line totals are kept in €0.0001, coupon totals in cents
/// - VAT is extracted per tax group from VAT-inclusive gross amounts
/// </summary>
public sealed class CouponBuilder
{
    public const int MaxVerificationNoLength = 16;

    private readonly TaxRateTable _rates;
    private readonly VatRounding _rounding;

    public CouponBuilder(TaxRateTable? rates = null, VatRounding rounding = VatRounding.RoundTaxHalfUp)
    {
        _rates = rates ?? new TaxRateTable();
        _rounding = rounding;
    }

    public PosCoupon Build(ReceiptRequest r)
    {
        var errors = new List<string>();
        ValidateHeader(r, errors);
        if (r.Lines.Count == 0) errors.Add("Receipt has no lines.");

        var coupon = new PosCoupon
        {
            BusinessId = r.BusinessId,
            CouponId = r.CouponId,
            BranchId = r.BranchId,
            Location = r.Location,
            OperatorId = r.OperatorId,
            PosId = r.PosId,
            ApplicationId = r.ApplicationId,
            VerificationNo = r.VerificationNo,
            Type = r.Type,
            Time = r.IssuedAt.ToUnixTimeSeconds(),
            ReferenceNo = r.ReferenceNo,
        };

        long totalDiscountUnits = 0;
        var grossByRate = new SortedDictionary<string, long>(StringComparer.Ordinal); // €0.0001

        foreach (var line in GroupIdenticalLines(r.Lines))
        {
            if (!_rates.IsKnown(line.TaxRate)) { errors.Add($"Line '{line.Name}': unknown tax rate '{line.TaxRate}'."); continue; }
            if (line.Quantity <= 0) { errors.Add($"Line '{line.Name}': quantity must be positive."); continue; }
            if (line.UnitPrice < 0) { errors.Add($"Line '{line.Name}': unit price cannot be negative."); continue; }
            if (decimal.Round(line.UnitPrice, 4) != line.UnitPrice) errors.Add($"Line '{line.Name}': unit price has more than 4 decimals.");

            var price = Money.ToPriceUnits(line.UnitPrice);
            var discount = Money.ToPriceUnits(line.Discount);
            var lineTotal = (long)decimal.Round(price * line.Quantity, 0, MidpointRounding.AwayFromZero) - discount;
            if (lineTotal < 0) { errors.Add($"Line '{line.Name}': discount exceeds line amount."); continue; }

            coupon.Items.Add(new CouponItem
            {
                Name = line.Name,
                Price = price,
                Unit = line.Unit,
                Quantity = (float)line.Quantity,
                Total = lineTotal,
                TaxRate = line.TaxRate,
                Type = line.Category,
            });

            totalDiscountUnits += discount;
            grossByRate[line.TaxRate] = grossByRate.GetValueOrDefault(line.TaxRate) + lineTotal;
        }

        long total = 0, totalTax = 0;
        foreach (var (letter, grossUnits) in grossByRate)
        {
            var gross = Money.PriceUnitsToCents(grossUnits);
            var (net, tax) = SplitVat(gross, _rates.PercentFor(letter));
            coupon.TaxGroups.Add(new TaxGroup { TaxRate = letter, TotalForTax = net, TotalTax = tax });
            total += gross;
            totalTax += tax;
        }

        coupon.Total = total;
        coupon.TotalTax = totalTax;
        coupon.TotalNoTax = total - totalTax;
        coupon.TotalDiscount = Money.PriceUnitsToCents(totalDiscountUnits);

        long paid = 0;
        foreach (var p in r.Payments)
        {
            if (p.Type == PaymentType.UnknownPayment) errors.Add("Payment type is required.");
            if (p.Amount <= 0) errors.Add("Payment amounts must be positive.");
            var cents = Money.ToCents(p.Amount);
            coupon.Payments.Add(new Payment { Type = p.Type, Amount = cents });
            paid += cents;
        }
        if (paid != total)
            errors.Add($"Payments ({Money.CentsToEuros(paid):0.00}) do not match receipt total ({Money.CentsToEuros(total):0.00}).");

        if (errors.Count > 0) throw new FiscalValidationException(errors);
        return coupon;
    }

    /// <summary>The subset of the PosCoupon that goes into the QR code. Must match the PosCoupon exactly.</summary>
    public static CitizenCoupon ToCitizenCoupon(PosCoupon pos)
    {
        var c = new CitizenCoupon
        {
            BusinessId = pos.BusinessId,
            CouponId = pos.CouponId,
            BranchId = pos.BranchId,
            PosId = pos.PosId,
            VerificationNo = pos.VerificationNo,
            Type = pos.Type,
            Time = pos.Time,
            Total = pos.Total,
            TotalTax = pos.TotalTax,
            TotalNoTax = pos.TotalNoTax,
        };
        c.TaxGroups.AddRange(pos.TaxGroups.Select(t => t.Clone()));
        return c;
    }

    public (long Net, long Tax) SplitVat(long grossCents, decimal ratePercent)
    {
        if (ratePercent == 0) return (grossCents, 0);
        switch (_rounding)
        {
            case VatRounding.TruncateNet:
                var net = (long)decimal.Floor(grossCents * 100m / (100m + ratePercent));
                return (net, grossCents - net);
            default:
                var tax = (long)decimal.Round(grossCents * ratePercent / (100m + ratePercent), 0, MidpointRounding.AwayFromZero);
                return (grossCents - tax, tax);
        }
    }

    private static void ValidateHeader(ReceiptRequest r, List<string> errors)
    {
        if (r.BusinessId == 0) errors.Add("BusinessId (NUI) is required.");
        if (r.BranchId == 0) errors.Add("BranchId is required.");
        if (r.PosId == 0) errors.Add("PosId is required.");
        if (r.ApplicationId == 0) errors.Add("ApplicationId is required.");
        if (r.CouponId == 0) errors.Add("CouponId is required.");
        if (string.IsNullOrWhiteSpace(r.VerificationNo) || r.VerificationNo.Length > MaxVerificationNoLength || !r.VerificationNo.All(char.IsAsciiLetterOrDigit))
            errors.Add($"VerificationNo must be 1-{MaxVerificationNoLength} alphanumeric characters.");
        if (r.Type == CouponType.UnknownType) errors.Add("Coupon type is required.");
        if (r.Type is CouponType.Return or CouponType.Cancel && r.ReferenceNo == 0)
            errors.Add("ReferenceNo (original CouponId) is required for return/cancel coupons.");
        if (r.Type == CouponType.Sale && r.ReferenceNo != 0)
            errors.Add("ReferenceNo must be 0 for sale coupons.");
    }

    private static IEnumerable<ReceiptLine> GroupIdenticalLines(IEnumerable<ReceiptLine> lines) =>
        lines
            .GroupBy(l => (l.Name.Trim(), l.Unit, l.UnitPrice, l.TaxRate, l.Category))
            .Select(g => g.First() with
            {
                Name = g.Key.Item1,
                Quantity = g.Sum(x => x.Quantity),
                Discount = g.Sum(x => x.Discount),
            });
}
