using System.Security.Cryptography;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Security;
using Hrs.Fiscal.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Receipts;

public sealed class DetailModel(ReceiptQueries receipts, PropertyClock clock, ManualReceipts manual, IAuthorizationService auth, UserStore users,
    SettingsStore settings, Printing.ReceiptPrinting printing) : PageModel
{
    /// <summary>Why the receipt cannot be cancelled (null = it can); only computed for users allowed to cancel.</summary>
    public string? CannotCancel { get; private set; }
    public bool CanCancel { get; private set; }
    /// <summary>The receipt's workstation has a thermal printer and the user may print copies.</summary>
    public bool CanPrint { get; private set; }
    public bool OriginalPrinted { get; private set; }
    public string PrinterLabel { get; private set; } = "";
    [BindProperty(SupportsGet = true)] public bool Cancel { get; set; }
    [BindProperty] public string? Reason { get; set; }
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
        if ((await auth.AuthorizeAsync(User, Permissions.Policy(Permissions.ReceiptsCancel))).Succeeded)
        {
            CannotCancel = await manual.CannotCancelAsync(r);
            CanCancel = CannotCancel is null;
        }
        if ((await auth.AuthorizeAsync(User, Permissions.Policy(Permissions.ReceiptsCopy))).Succeeded
            && (await settings.TerminalsAsync()).SingleOrDefault(t => t.Id == r.TerminalId) is { HasPrinter: true } terminal)
        {
            CanPrint = true;
            PrinterLabel = Printing.ReceiptPrinting.Describe(terminal);
            OriginalPrinted = await printing.OriginalPrintedAsync(id);
        }
        return Page();
    }

    /// <summary>Prints the receipt on its workstation's thermal printer: the original if it was never printed, else a copy.</summary>
    public async Task<IActionResult> OnPostPrintAsync(long id)
    {
        if (!(await auth.AuthorizeAsync(User, Permissions.Policy(Permissions.ReceiptsCopy))).Succeeded) return Forbid();
        var outcome = await printing.PrintAsync(id, User.Identity!.Name!);
        TempData[outcome.Ok ? "Message" : "Error"] = outcome.Message;
        return Redirect($"/Receipts/Detail/{id}");
    }

    /// <summary>Issues a cancellation receipt ("Kupon i anulimit") for this sale.</summary>
    public async Task<IActionResult> OnPostCancelAsync(long id)
    {
        if (!(await auth.AuthorizeAsync(User, Permissions.Policy(Permissions.ReceiptsCancel))).Succeeded) return Forbid();
        var name = User.Identity!.Name!;
        var display = (await users.AllAsync()).FirstOrDefault(u => u.Username == name)?.DisplayName ?? name;
        try
        {
            var result = await manual.CancelAsync(id, Reason ?? "", name, $"{display} ({name})");
            TempData[result.Outcome == AtkOutcome.Rejected ? "Error" : "Message"] = result.Outcome switch
            {
                AtkOutcome.Accepted => $"Cancellation receipt {result.CouponId} issued and accepted by ATK.",
                AtkOutcome.Rejected => $"Cancellation receipt {result.CouponId} was rejected by ATK: {result.AtkMessage}",
                _ => $"Cancellation receipt {result.CouponId} issued; ATK could not be reached, it is sent automatically (OFFLINE).",
            };
            return Redirect($"/Receipts/Detail/{result.ReceiptId}");
        }
        catch (FiscalValidationException ex)
        {
            TempData["Error"] = string.Join(" ", ex.Errors);
            return Redirect($"/Receipts/Detail/{id}?Cancel=true");
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return Redirect($"/Receipts/Detail/{id}");
        }
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
