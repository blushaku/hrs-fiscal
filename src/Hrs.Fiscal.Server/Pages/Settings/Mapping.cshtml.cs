using System.Text.RegularExpressions;
using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Settings;

/// <summary>
/// Transaction codes, payment methods and VAT rates. Lists are read-only; "Add" or "Edit" opens a form
/// (?Form=trx|pay|vat, &amp;Key=… for editing) that is saved with its own Save button.
/// </summary>
public sealed class MappingModel(SettingsStore settings) : PageModel
{
    public static readonly (short Value, string Label)[] AtkPaymentTypes =
        [(1, "Cash"), (2, "Credit/debit card"), (3, "Voucher"), (4, "Cheque"), (5, "Crypto currency"), (6, "Other")];

    // ATK goods/service categories (technical requirements annex); free text is allowed for others.
    public static readonly (string Code, string Label)[] SuggestedCategories =
    [
        ("HT", "Hotels and tourism"), ("UR", "Restaurant food"), ("PJA", "Non-alcoholic drinks"), ("PAL", "Alcoholic drinks"),
        ("DUH", "Tobacco"), ("SUA", "Travel and accommodation services"), ("SHZ", "Beauty services"), ("ST", "Other services"), ("TT", "Other"),
    ];

    public IReadOnlyList<TrxMapping> Trx { get; private set; } = [];
    public IReadOnlyList<PaymentMapping> Payments { get; private set; } = [];
    public IReadOnlyList<VatRateRow> Vat { get; private set; } = [];
    public bool OverridesEnabled { get; private set; }

    /// <summary>Which form is open: trx, pay or vat (null = none).</summary>
    [BindProperty(SupportsGet = true)] public string? Form { get; set; }
    /// <summary>Key of the row being edited; null = adding.</summary>
    [BindProperty(SupportsGet = true)] public string? Key { get; set; }
    public bool Editing => !string.IsNullOrEmpty(Key);

    [BindProperty] public TrxMapping TrxInput { get; set; } = new();
    [BindProperty] public PaymentMapping PaymentInput { get; set; } = new();
    [BindProperty] public VatRateRow VatInput { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadAsync();
        if (!Editing) return;
        switch (Form)
        {
            case "trx" when Trx.SingleOrDefault(t => t.TrxCode == Key) is { } t: TrxInput = t; break;
            case "pay" when Payments.SingleOrDefault(p => p.PaymentCode == Key) is { } p: PaymentInput = p; break;
            case "vat" when Vat.SingleOrDefault(v => v.Letter == Key) is { } v: VatInput = v; break;
            default: Form = null; Key = null; break;
        }
    }

    public async Task<IActionResult> OnPostTrxAsync()
    {
        await LoadAsync();
        Form = "trx";
        var exists = Trx.Any(t => t.TrxCode == TrxInput.TrxCode?.Trim());
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(TrxInput.TrxCode)) errors.Add("OPERA transaction code is required.");
        else if (!Editing && exists) errors.Add($"Transaction code {TrxInput.TrxCode.Trim()} already exists; edit it in the list.");
        if (string.IsNullOrWhiteSpace(TrxInput.ItemName)) errors.Add("Receipt item name is required.");
        if (string.IsNullOrWhiteSpace(TrxInput.Category)) errors.Add("ATK category is required.");
        if (!Vat.Any(v => v.Letter == TrxInput.VatLetter)) errors.Add("Choose a VAT rate.");
        if (errors.Count > 0) return Error(errors);

        TrxInput.TrxCode = TrxInput.TrxCode.Trim();
        TrxInput.ItemName = TrxInput.ItemName.Trim();
        TrxInput.Unit = string.IsNullOrWhiteSpace(TrxInput.Unit) ? "cope" : TrxInput.Unit.Trim();
        TrxInput.Category = TrxInput.Category.Trim().ToUpperInvariant();
        await settings.SaveTrxMappingAsync(TrxInput, User.Identity!.Name!);
        TempData["Message"] = $"Transaction code {TrxInput.TrxCode} {(Editing ? "saved" : "added")} and logged.";
        return RedirectToPage(new { Form = (string?)null, Key = (string?)null });
    }

    public async Task<IActionResult> OnPostPaymentAsync()
    {
        await LoadAsync();
        Form = "pay";
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(PaymentInput.PaymentCode)) errors.Add("OPERA payment code is required.");
        else if (!Editing && Payments.Any(p => p.PaymentCode == PaymentInput.PaymentCode.Trim())) errors.Add($"Payment code {PaymentInput.PaymentCode.Trim()} already exists; edit it in the list.");
        if (string.IsNullOrWhiteSpace(PaymentInput.Description)) errors.Add("Description is required.");
        if (PaymentInput.AtkPaymentType is < 1 or > 6) errors.Add("Choose the ATK payment type.");
        if (errors.Count > 0) return Error(errors);
        PaymentInput.PaymentCode = PaymentInput.PaymentCode.Trim();
        PaymentInput.Description = PaymentInput.Description.Trim();
        await settings.SavePaymentMappingAsync(PaymentInput, User.Identity!.Name!);
        TempData["Message"] = $"Payment method {PaymentInput.PaymentCode} {(Editing ? "saved" : "added")} and logged.";
        return RedirectToPage(new { Form = (string?)null, Key = (string?)null });
    }

    public async Task<IActionResult> OnPostVatAsync()
    {
        await LoadAsync();
        Form = "vat";
        VatInput.Letter = (VatInput.Letter ?? "").Trim().ToUpperInvariant();
        var errors = new List<string>();
        if (!Regex.IsMatch(VatInput.Letter, "^[A-Z]$")) errors.Add("The VAT letter is one capital letter (A–Z).");
        else if (!Editing && Vat.Any(v => v.Letter == VatInput.Letter)) errors.Add($"VAT letter {VatInput.Letter} already exists; edit it in the list.");
        if (VatInput.Percent is < 0 or >= 100) errors.Add("VAT percent must be between 0 and 99.99.");
        if (string.IsNullOrWhiteSpace(VatInput.Description)) errors.Add("Description is required.");
        if (errors.Count > 0) return Error(errors);
        VatInput.Description = VatInput.Description.Trim();
        try
        {
            if (Editing) await settings.SaveVatRateAsync(VatInput, User.Identity!.Name!);
            else await settings.AddVatRateAsync(VatInput, User.Identity!.Name!);
        }
        catch (InvalidOperationException ex) { return Error([ex.Message]); }
        TempData["Message"] = $"VAT rate {VatInput.Letter} {(Editing ? "saved" : "added")} and logged. It applies to receipts issued from now on.";
        return RedirectToPage(new { Form = (string?)null, Key = (string?)null });
    }

    private PageResult Error(IEnumerable<string> errors)
    {
        foreach (var e in errors) ModelState.AddModelError("", e);
        return Page();
    }

    private async Task LoadAsync()
    {
        Trx = await settings.TrxMappingsAsync();
        Payments = await settings.PaymentMappingsAsync();
        Vat = await settings.VatRatesAsync();
        OverridesEnabled = await settings.OperaOverridesEnabledAsync();
    }

    public string PaymentLabel(short type) => AtkPaymentTypes.FirstOrDefault(x => x.Value == type).Label ?? type.ToString();
}
