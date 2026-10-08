using System.Security.Cryptography;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Receipts;

public sealed class DetailModel(ReceiptQueries receipts, PropertyClock clock) : PageModel
{
    public ReceiptDetail R { get; private set; } = null!;
    public PosCoupon Coupon { get; private set; } = null!;
    public IReadOnlyList<TransmissionRow> Attempts { get; private set; } = [];
    public IReadOnlyList<ReceiptListRow> Related { get; private set; } = [];
    public SourcePayloadRow? Payload { get; private set; }
    public string PayloadSha { get; private set; } = "";
    public PropertyClock Clock => clock;

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var r = await receipts.GetAsync(id);
        if (r is null) return NotFound();
        R = r;
        Coupon = PosCoupon.Parser.ParseFrom(r.PosCoupon);
        Attempts = await receipts.TransmissionsAsync(id);
        Related = await receipts.RelatedAsync(r);
        Payload = await receipts.SourcePayloadAsync(id);
        if (Payload is not null)
            PayloadSha = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Payload.Body))).ToLowerInvariant();
        return Page();
    }

    public static string PaymentLabel(PaymentType t) => t switch
    {
        PaymentType.Cash => "Cash",
        PaymentType.CreditCard => "Card",
        PaymentType.Voucher => "Voucher",
        PaymentType.Cheque => "Cheque",
        PaymentType.CryptoCurrency => "Crypto",
        _ => "Other",
    };
}
