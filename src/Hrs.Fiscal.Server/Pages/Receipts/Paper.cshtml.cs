using System.ComponentModel.DataAnnotations;
using Hrs.Fiscal.Core;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Security;
using Hrs.Fiscal.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Receipts;

/// <summary>
/// Entry of receipts written from the special paper block while the workstation or the fiscal server was down
/// (UA Art 45; Doc. 01-06-1967 Art 25.16). The receipt is signed with the workstation's key, keeps the paper serial and
/// the time written on the paper, and is sent to ATK like any receipt.
/// </summary>
[Authorize(Policy = "perm:" + Permissions.ReceiptsPaper)]
public sealed class PaperModel(SettingsStore settings, ManualReceipts manual, UserStore users, PropertyClock clock) : PageModel
{
    public const int Rows = 8;

    public sealed class LineInput
    {
        public string? Name { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? UnitPrice { get; set; }
        public string? TaxRate { get; set; }
        public string? Unit { get; set; }
    }

    [BindProperty, Required(ErrorMessage = "Choose the workstation.")] public long? PosId { get; set; }
    [BindProperty, Required(ErrorMessage = "Enter the serial number printed on the paper receipt.")] public string? Serial { get; set; }
    [BindProperty, Required(ErrorMessage = "Enter the date and time written on the paper receipt.")] public DateTime? IssuedAt { get; set; }
    [BindProperty, Required(ErrorMessage = "Enter who wrote the paper receipt.")] public string? Operator { get; set; }
    [BindProperty] public PaymentType Payment { get; set; } = PaymentType.Cash;
    [BindProperty] public List<LineInput> Lines { get; set; } = [];

    public IReadOnlyList<TerminalEdit> Terminals { get; private set; } = [];
    public IReadOnlyList<VatRateRow> Vat { get; private set; } = [];

    private async Task LoadAsync()
    {
        Terminals = await settings.TerminalsAsync();
        Vat = await settings.VatRatesAsync();
        while (Lines.Count < Rows) Lines.Add(new LineInput { Quantity = 1, Unit = "cope" });
    }

    public async Task OnGetAsync()
    {
        var name = User.Identity!.Name!;
        Operator = (await users.AllAsync()).FirstOrDefault(u => u.Username == name)?.DisplayName;
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var lines = Lines.Where(l => !string.IsNullOrWhiteSpace(l.Name)).ToList();
        foreach (var (l, i) in lines.Select((l, i) => (l, i + 1)))
        {
            if (l.Quantity is not > 0) ModelState.AddModelError("", $"Line {i}: enter a quantity above zero.");
            if (l.UnitPrice is not > 0) ModelState.AddModelError("", $"Line {i}: enter the unit price (VAT included).");
            if (string.IsNullOrEmpty(l.TaxRate)) ModelState.AddModelError("", $"Line {i}: choose the VAT rate.");
        }
        if (lines.Count == 0) ModelState.AddModelError("", "Enter at least one line.");
        if (!ModelState.IsValid) { await LoadAsync(); return Page(); }

        var actor = User.Identity!.Name!;
        try
        {
            var result = await manual.EnterPaperAsync(PosId!.Value, Serial!, IssuedAt!.Value, Operator!.Trim(),
                lines.Select(l => new PaperLine(l.Name!, l.Quantity!.Value, decimal.Round(l.UnitPrice!.Value, 4), l.TaxRate!, l.Unit ?? "cope", "TT")).ToList(),
                Payment, actor);
            var late = clock.LocalNow - IssuedAt.Value > ManualReceipts.PaperDeadline;
            TempData[result.Outcome == AtkOutcome.Rejected ? "Error" : "Message"] = (result.Outcome switch
            {
                AtkOutcome.Accepted => $"Paper receipt {Serial} entered as receipt {result.CouponId} and accepted by ATK.",
                AtkOutcome.Rejected => $"Paper receipt {Serial} was entered as receipt {result.CouponId} but ATK rejected it: {result.AtkMessage}",
                _ => $"Paper receipt {Serial} entered as receipt {result.CouponId}; ATK could not be reached, it is sent automatically.",
            }) + (late ? " It was entered more than 7 days after it was written: keep the reason with the paper block." : "");
            return Redirect($"/Receipts/Detail/{result.ReceiptId}");
        }
        catch (Exception ex) when (ex is FiscalValidationException or InvalidOperationException)
        {
            foreach (var e in ex is FiscalValidationException f ? f.Errors : [ex.Message]) ModelState.AddModelError("", e);
            await LoadAsync();
            return Page();
        }
    }
}
