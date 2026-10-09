using Hrs.Fiscal.Core;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Receipts;

namespace Hrs.Fiscal.Core.Tests;

public class CouponVerifierTests
{
    private static PosCoupon Sample() => new CouponBuilder().Build(new ReceiptRequest
    {
        BusinessId = 811159898, BranchId = 1, PosId = 1, ApplicationId = 1, CouponId = 10000000001, VerificationNo = "ABCDEFGHJKMNPQRS",
        Location = "Prishtinë", OperatorId = "test", IssuedAt = DateTimeOffset.UnixEpoch.AddYears(56),
        Lines =
        [
            new ReceiptLine { Name = "Room", Unit = "nate", Quantity = 2, UnitPrice = 85m, TaxRate = "D", Category = "HT" },
            new ReceiptLine { Name = "Dinner", Unit = "cope", Quantity = 1, UnitPrice = 23.6m, TaxRate = "E", Category = "UR" },
        ],
        Payments = [new ReceiptPayment(PaymentType.CreditCard, 193.6m)],
    });

    [Fact]
    public void A_correct_receipt_passes() => Assert.Empty(CouponVerifier.Verify(Sample(), new TaxRateTable()));

    [Fact]
    public void Wrong_vat_total_line_total_and_payment_are_reported()
    {
        var c = Sample();
        c.TaxGroups[0].TotalTax += 500;          // VAT D off by 5.00
        c.Items[1].Total += 10_000;              // dinner 1.00 more than price × qty
        c.Payments[0].Amount -= 100;             // 1.00 short
        var errors = CouponVerifier.Verify(c, new TaxRateTable());
        Assert.Contains(errors, e => e.Contains("VAT group D"));
        Assert.Contains(errors, e => e.Contains("'Dinner'") && e.Contains("more than price"));
        Assert.Contains(errors, e => e.Contains("Payments"));
    }
}
