using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Settings;

public sealed class MappingModel(SettingsStore settings) : PageModel
{
    public static readonly (short Value, string Label)[] AtkPaymentTypes =
        [(1, "Cash"), (2, "Credit/debit card"), (3, "Voucher"), (4, "Cheque"), (5, "Crypto currency"), (6, "Other")];

    // ATK goods/service categories seen in the technical requirements annex; free text is allowed for others.
    public static readonly string[] SuggestedCategories = ["HT", "SUA", "UR", "TT"];

    public IReadOnlyList<TrxMapping> Trx { get; private set; } = [];
    public IReadOnlyList<PaymentMapping> Payments { get; private set; } = [];
    public IReadOnlyList<VatRateRow> Vat { get; private set; } = [];

    [BindProperty] public TrxMapping TrxInput { get; set; } = new();
    [BindProperty] public PaymentMapping PaymentInput { get; set; } = new();
    [BindProperty] public VatRateRow VatInput { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? EditTrx { get; set; }
    [BindProperty(SupportsGet = true)] public string? EditPayment { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        if (EditTrx is not null && Trx.SingleOrDefault(t => t.TrxCode == EditTrx) is { } t) TrxInput = t;
        if (EditPayment is not null && Payments.SingleOrDefault(p => p.PaymentCode == EditPayment) is { } p) PaymentInput = p;
    }

    public async Task<IActionResult> OnPostTrxAsync()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(TrxInput.TrxCode)) errors.Add("OPERA transaction code is required.");
        if (string.IsNullOrWhiteSpace(TrxInput.ItemName)) errors.Add("Receipt item name is required.");
        if (string.IsNullOrWhiteSpace(TrxInput.Category)) errors.Add("ATK category is required.");
        if (TrxInput.VatLetter is not ("A" or "C" or "D" or "E")) errors.Add("Choose a VAT letter.");
        if (errors.Count > 0) return await ErrorAsync(errors);

        TrxInput.TrxCode = TrxInput.TrxCode.Trim();
        TrxInput.ItemName = TrxInput.ItemName.Trim();
        TrxInput.Unit = string.IsNullOrWhiteSpace(TrxInput.Unit) ? "cope" : TrxInput.Unit.Trim();
        TrxInput.Category = TrxInput.Category.Trim().ToUpperInvariant();
        await settings.SaveTrxMappingAsync(TrxInput, User.Identity!.Name!);
        TempData["Message"] = $"Mapping for transaction code {TrxInput.TrxCode} saved and logged.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPaymentAsync()
    {
        if (string.IsNullOrWhiteSpace(PaymentInput.PaymentCode) || string.IsNullOrWhiteSpace(PaymentInput.Description) || PaymentInput.AtkPaymentType is < 1 or > 6)
            return await ErrorAsync(["Payment code, description and ATK payment type are required."]);
        PaymentInput.PaymentCode = PaymentInput.PaymentCode.Trim();
        await settings.SavePaymentMappingAsync(PaymentInput, User.Identity!.Name!);
        TempData["Message"] = $"Payment method {PaymentInput.PaymentCode} saved and logged.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostVatAsync()
    {
        if (VatInput.Percent is < 0 or >= 100) return await ErrorAsync(["VAT percent must be between 0 and 99.99."]);
        await settings.SaveVatRateAsync(VatInput, User.Identity!.Name!);
        TempData["Message"] = $"VAT rate {VatInput.Letter} saved and logged. It applies to receipts issued from now on.";
        return RedirectToPage();
    }

    private async Task<IActionResult> ErrorAsync(IEnumerable<string> errors)
    {
        foreach (var e in errors) ModelState.AddModelError("", e);
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Trx = await settings.TrxMappingsAsync();
        Payments = await settings.PaymentMappingsAsync();
        Vat = await settings.VatRatesAsync();
    }
}
