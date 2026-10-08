using Hrs.Fiscal.Core;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Receipts;

namespace Hrs.Fiscal.Core.Tests;

public class CouponBuilderTests
{
    internal static ReceiptRequest AtkSampleRequest(CouponType type = CouponType.Sale, ulong referenceNo = 0) => new()
    {
        BusinessId = 60100,
        BranchId = 1,
        PosId = 1,
        ApplicationId = 1234,
        CouponId = 10,
        VerificationNo = "1234567890123456",
        Location = "Prishtine",
        OperatorId = "Kushtrimi",
        IssuedAt = new DateTimeOffset(2024, 10, 1, 15, 30, 20, TimeSpan.Zero),
        Type = type,
        ReferenceNo = referenceNo,
        Lines =
        [
            new ReceiptLine { Name = "uje rugove", Unit = "cope", Quantity = 3, UnitPrice = 1.50m, TaxRate = "C", Category = "TT" },
            new ReceiptLine { Name = "sendviq", Unit = "cope", Quantity = 2, UnitPrice = 3.00m, TaxRate = "E", Category = "TT" },
            new ReceiptLine { Name = "buke", Unit = "cope", Quantity = 4, UnitPrice = 0.80m, TaxRate = "D", Category = "TT" },
            new ReceiptLine { Name = "machiato e madhe", Unit = "cope", Quantity = 3, UnitPrice = 1.50m, TaxRate = "E", Category = "TT" },
        ],
        Payments =
        [
            new ReceiptPayment(PaymentType.Cash, 5.00m),
            new ReceiptPayment(PaymentType.CreditCard, 10.00m),
            new ReceiptPayment(PaymentType.Voucher, 3.20m),
        ],
    };

    [Fact]
    public void Reproduces_atk_sample_totals_with_truncate_net_rounding()
    {
        var coupon = new CouponBuilder(rounding: VatRounding.TruncateNet).Build(AtkSampleRequest());

        // Values from github.com/fiskalizimi/pos-csharp ModelBuilder.GetPosCoupon()
        Assert.Equal(1820, coupon.Total);
        Assert.Equal(185, coupon.TotalTax);
        Assert.Equal(1635, coupon.TotalNoTax);
        Assert.Collection(coupon.TaxGroups,
            g => { Assert.Equal("C", g.TaxRate); Assert.Equal(450, g.TotalForTax); Assert.Equal(0, g.TotalTax); },
            g => { Assert.Equal("D", g.TaxRate); Assert.Equal(296, g.TotalForTax); Assert.Equal(24, g.TotalTax); },
            g => { Assert.Equal("E", g.TaxRate); Assert.Equal(889, g.TotalForTax); Assert.Equal(161, g.TotalTax); });
        Assert.Equal(45000, coupon.Items.Single(i => i.Name == "uje rugove").Total); // €4.50 in €0.0001
        Assert.Equal(15000, coupon.Items.Single(i => i.Name == "uje rugove").Price);
    }

    [Fact]
    public void Default_rounding_rounds_tax_half_up()
    {
        var coupon = new CouponBuilder().Build(AtkSampleRequest());
        var e = coupon.TaxGroups.Single(g => g.TaxRate == "E");
        Assert.Equal(160, e.TotalTax);   // 1050 * 18/118 = 160.17
        Assert.Equal(890, e.TotalForTax);
        Assert.Equal(coupon.Total, coupon.TotalNoTax + coupon.TotalTax);
    }

    [Fact]
    public void Merges_identical_lines()
    {
        var req = AtkSampleRequest() with
        {
            Lines =
            [
                new ReceiptLine { Name = "Room night", Unit = "nate", Quantity = 1, UnitPrice = 80m, TaxRate = "D", Category = "HT" },
                new ReceiptLine { Name = "Room night", Unit = "nate", Quantity = 1, UnitPrice = 80m, TaxRate = "D", Category = "HT" },
                new ReceiptLine { Name = "Room night", Unit = "nate", Quantity = 1, UnitPrice = 90m, TaxRate = "D", Category = "HT" },
            ],
            Payments = [new ReceiptPayment(PaymentType.CreditCard, 250m)],
        };

        var coupon = new CouponBuilder().Build(req);

        Assert.Equal(2, coupon.Items.Count);
        Assert.Equal(2f, coupon.Items.Single(i => i.Price == 800000).Quantity);
        Assert.Equal(25000, coupon.Total);
    }

    [Fact]
    public void Line_discount_reduces_total_and_is_reported()
    {
        var req = AtkSampleRequest() with
        {
            Lines = [new ReceiptLine { Name = "Dinner", Unit = "cope", Quantity = 2, UnitPrice = 12.5m, Discount = 5m, TaxRate = "E", Category = "UR" }],
            Payments = [new ReceiptPayment(PaymentType.Cash, 20m)],
        };

        var coupon = new CouponBuilder().Build(req);

        Assert.Equal(2000, coupon.Total);
        Assert.Equal(500, coupon.TotalDiscount);
    }

    [Fact]
    public void Return_requires_reference_to_original()
    {
        var ex = Assert.Throws<FiscalValidationException>(() => new CouponBuilder().Build(AtkSampleRequest(CouponType.Return)));
        Assert.Contains(ex.Errors, e => e.Contains("ReferenceNo"));

        var ok = new CouponBuilder().Build(AtkSampleRequest(CouponType.Return, referenceNo: 9));
        Assert.Equal(9ul, ok.ReferenceNo);
    }

    [Fact]
    public void Rejects_payment_mismatch_unknown_rate_and_long_verification_no()
    {
        var req = AtkSampleRequest() with
        {
            VerificationNo = "12345678901234567",
            Lines = [new ReceiptLine { Name = "x", Unit = "cope", Quantity = 1, UnitPrice = 1m, TaxRate = "Z", Category = "TT" }],
            Payments = [new ReceiptPayment(PaymentType.Cash, 2m)],
        };

        var ex = Assert.Throws<FiscalValidationException>(() => new CouponBuilder().Build(req));
        Assert.Contains(ex.Errors, e => e.Contains("VerificationNo"));
        Assert.Contains(ex.Errors, e => e.Contains("unknown tax rate"));
        Assert.Contains(ex.Errors, e => e.Contains("do not match"));
    }

    [Fact]
    public void Citizen_coupon_matches_pos_coupon()
    {
        var pos = new CouponBuilder().Build(AtkSampleRequest());
        var citizen = CouponBuilder.ToCitizenCoupon(pos);

        Assert.Equal(pos.CouponId, citizen.CouponId);
        Assert.Equal(pos.VerificationNo, citizen.VerificationNo);
        Assert.Equal(pos.Total, citizen.Total);
        Assert.Equal(pos.TaxGroups, citizen.TaxGroups);
    }

    [Fact]
    public void Verification_numbers_are_16_unambiguous_characters()
    {
        var all = Enumerable.Range(0, 1000).Select(_ => VerificationNumber.New()).ToList();
        Assert.All(all, v => Assert.Equal(16, v.Length));
        Assert.All(all, v => Assert.DoesNotContain(v, c => "01OIL".Contains(c)));
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
