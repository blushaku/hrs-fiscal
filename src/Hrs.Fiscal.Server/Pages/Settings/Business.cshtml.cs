using System.ComponentModel.DataAnnotations;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Signing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Settings;

/// <summary>
/// Business & unit. Setup and changes run in two steps: (1) the values ATK issued — NUI, fiscalization number, unit number,
/// Application ID — are validated with ATK's registration check; (2) only then the rest of the form opens, with the
/// registered name taken from ATK's answer. Saving needs a validation of exactly the values being saved, unless they are
/// unchanged from what is already saved (then only the other fields are edited).
/// </summary>
public sealed class BusinessModel(SettingsStore settings, BusinessReset reset, IAtkClientFactory atkFactory, AuditLog audit,
    IDataProtectionProvider protection) : PageModel
{
    public sealed class Form
    {
        // Step 1: issued by ATK, validated with ATK.
        [Range(1, 999_999_999_999, ErrorMessage = "Enter the NUI.")] public long Nui { get; set; }
        [Required(ErrorMessage = "Enter the fiscalization number.")] public string FiscalizationNo { get; set; } = "";
        [Range(1, 899_999_999, ErrorMessage = "Unit number must be between 1 and 899999999.")] public long BranchId { get; set; }
        [Range(1, long.MaxValue, ErrorMessage = "Enter the Application ID issued by ATK.")] public long ApplicationId { get; set; }
        // Step 2.
        [Required(ErrorMessage = "Enter the registered name.")] public string Name { get; set; } = "";
        public string? VatNo { get; set; }
        [Required(ErrorMessage = "Enter the unit name.")] public string BranchName { get; set; } = "";
        [Required(ErrorMessage = "Enter the city printed on receipts.")] public string Location { get; set; } = "";
        public string? Address { get; set; }
        public string? OperaHotelCode { get; set; }
    }

    private static readonly string[] Step2Fields = [nameof(Form.Name), nameof(Form.VatNo), nameof(Form.BranchName), nameof(Form.Location), nameof(Form.Address), nameof(Form.OperaHotelCode)];
    private static readonly TimeSpan ValidationLifetime = TimeSpan.FromHours(2);
    private IDataProtector Protector => protection.CreateProtector("business-atk-validation");

    [BindProperty] public Form Input { get; set; } = new();
    /// <summary>Proof that ATK confirmed the step-1 values (protected, time-limited).</summary>
    [BindProperty] public string? Validation { get; set; }
    [BindProperty] public long PosId { get; set; }
    [BindProperty(SupportsGet = true)] public bool Edit { get; set; }
    [BindProperty(SupportsGet = true)] public bool Reset { get; set; }

    public string AtkEnvironment { get; private set; } = "Test";
    public long SuggestedPos { get; private set; } = 1;
    public long ApplicationId { get; private set; }
    public int Workstations { get; private set; }
    public long WaitingToSend { get; private set; }
    public long Receipts { get; private set; }
    /// <summary>Ever connected to ATK Production: then the data are legal records and cannot be wiped here.</summary>
    public bool ProductionUsed { get; private set; }
    public BusinessInfo? Current { get; private set; }
    public string? AtkName { get; private set; }
    public string? AtkNameSource { get; private set; }
    public bool NameMatchesAtk => AtkName is null || Current is null || TerminalEnrollment.SameName(AtkName, Current.Name);
    /// <summary>Result of the last validation in this request (shown in the dialog).</summary>
    public string? ValidationMessage { get; private set; }
    public bool ValidationFailed { get; private set; }
    public bool Editing => (Edit || !ModelState.IsValid || ValidationMessage is not null) && !Reset;

    /// <summary>Step 1 equals what is saved: the rest can be edited without a new ATK check.</summary>
    public bool Step1Unchanged => Current is not null && Input.Nui == Current.Nui && Input.BranchId == Current.BranchId
                                  && string.Equals(Input.FiscalizationNo?.Trim(), Current.FiscalizationNo, StringComparison.Ordinal)
                                  && Input.ApplicationId == ApplicationId && ApplicationId > 0;
    public bool Step1Validated => ValidatedAtkName(Validation) is not null;
    public bool Step2Open => Step1Unchanged || Step1Validated;

    private string Step1Key() => $"{Input.Nui}|{Input.FiscalizationNo?.Trim()}|{Input.BranchId}|{Input.ApplicationId}|{AtkEnvironment}";

    /// <summary>The name ATK answered with, if the token confirms exactly the current step-1 values and has not expired.</summary>
    private string? ValidatedAtkName(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var parts = Protector.Unprotect(token).Split('\n', 3);
            return parts.Length == 3 && parts[0] == Step1Key()
                   && DateTime.UtcNow - new DateTime(long.Parse(parts[1]), DateTimeKind.Utc) < ValidationLifetime
                ? parts[2] : null;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException) { return null; }
    }

    private async Task LoadAsync()
    {
        Current = await settings.BusinessAsync();
        AtkName = await settings.GetAsync<string>("atk_business_name", null);
        if (string.IsNullOrWhiteSpace(AtkName)) AtkName = null;
        AtkNameSource = await settings.GetAsync<string>("atk_business_name_source", null);
        ApplicationId = await settings.GetAsync<long>("atk_application_id", 0);
        var terminals = await settings.TerminalsAsync();
        Workstations = terminals.Count;
        WaitingToSend = await settings.WaitingToSendAsync();
        AtkEnvironment = await settings.GetAsync("atk_environment", "Test") ?? "Test";
        var used = terminals.Select(t => t.PosId).ToHashSet();
        SuggestedPos = Enumerable.Range(1, 9999).Select(i => (long)i).First(p => !used.Contains(p));
        (Receipts, ProductionUsed) = await reset.StatusAsync();
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        var b = Current;
        Input = b is null
            ? new Form { ApplicationId = ApplicationId }
            : new Form
            {
                Nui = b.Nui, Name = b.Name, FiscalizationNo = b.FiscalizationNo, VatNo = b.VatNo, BranchId = b.BranchId,
                BranchName = b.BranchName, Location = b.Location, Address = b.Address, OperaHotelCode = b.OperaHotelCode,
                ApplicationId = ApplicationId,
            };
        PosId = SuggestedPos;
    }

    /// <summary>Step 1: ask ATK whether NUI, fiscalization no., unit and Application ID belong together.</summary>
    public async Task<IActionResult> OnPostValidateAsync()
    {
        await LoadAsync();
        Edit = true;
        foreach (var f in Step2Fields) ModelState.Remove($"{nameof(Input)}.{f}");
        ModelState.Remove(nameof(Validation));   // the field shows the new result, not the posted one
        Validation = null;
        if (!ModelState.IsValid) return Page();
        if (PosId <= 0) PosId = SuggestedPos;

        var environment = Enum.Parse<AtkEnvironment>(AtkEnvironment);
        var timeout = TimeSpan.FromSeconds(Math.Max(10, await settings.GetAsync("atk_timeout_seconds", 10)));
        var request = new VerifyRequest(Input.FiscalizationNo.Trim(), (ulong)PosId, (ulong)Input.BranchId, (ulong)Input.ApplicationId);
        var actor = User.Identity!.Name!;
        var details = new { environment = AtkEnvironment, Input.Nui, FiscalizationNo = Input.FiscalizationNo.Trim(), Input.BranchId, Input.ApplicationId, PosId };
        try
        {
            var verify = await atkFactory.Create(environment, timeout).VerifyAsync((ulong)Input.Nui, request);
            var atkName = verify.BusinessName.Trim();
            await audit.WriteAsync(actor, AuditLog.Actions.AtkCheck, "business", Input.Nui.ToString(), new { details, result = "confirmed", atkName });
            Validation = Protector.Protect($"{Step1Key()}\n{DateTime.UtcNow.Ticks}\n{atkName}");
            ModelState.Remove($"{nameof(Input)}.{nameof(Form.Name)}");
            if (!string.IsNullOrEmpty(atkName)) Input.Name = atkName;   // the registered name, as ATK has it
            ValidationMessage = $"ATK ({AtkEnvironment}) confirms these values: registered to “{atkName}”. Complete the details below and save.";
        }
        catch (Exception ex) when (ex is AtkApiException or HttpRequestException or TaskCanceledException)
        {
            ValidationFailed = true;
            Validation = null;
            ValidationMessage = ex switch
            {
                AtkApiException { Status: 404 } a => $"ATK did not accept this combination ({a.Message}). Check NUI, fiscalization number, unit number and Application ID; the fiscalization number may also have expired, or POS {PosId} is already registered (try another POS number).",
                AtkApiException a => $"ATK answered with an error: {a.Message}",
                TaskCanceledException => "ATK did not answer in time. Try again.",
                _ => $"ATK could not be reached: {ex.Message}",
            };
            await audit.WriteAsync(actor, AuditLog.Actions.AtkCheck, "business", Input.Nui.ToString(), new { details, result = "failed", reason = ValidationMessage });
        }
        return Page();
    }

    /// <summary>Step 2: save. New or changed ATK values need a matching validation.</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        Edit = true;
        if (!ModelState.IsValid) return Page();
        if (!Step2Open)
        {
            ModelState.AddModelError("", "Validate NUI, fiscalization number, unit number and Application ID with ATK first.");
            return Page();
        }
        var info = new BusinessInfo
        {
            Nui = Input.Nui, Name = Input.Name.Trim(), FiscalizationNo = Input.FiscalizationNo.Trim(), VatNo = Input.VatNo?.Trim(),
            BranchId = Input.BranchId, BranchName = Input.BranchName.Trim(), Location = Input.Location.Trim(),
            Address = Input.Address?.Trim(), OperaHotelCode = Input.OperaHotelCode?.Trim(),
        };
        var switching = Current is not null && (Current.Nui != info.Nui || Current.BranchId != info.BranchId);
        try
        {
            if (switching) await settings.ChangeTaxpayerAsync(info, User.Identity!.Name!);
            else await settings.SaveBusinessAsync(info, User.Identity!.Name!);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return Page();
        }
        var actor = User.Identity!.Name!;
        await settings.SetManyAsync(new Dictionary<string, object?> { ["atk_application_id"] = Input.ApplicationId }, actor);
        var atkName = ValidatedAtkName(Validation);
        if (!string.IsNullOrEmpty(atkName))
            await settings.SetManyAsync(new Dictionary<string, object?>
            {
                ["atk_business_name"] = atkName,
                ["atk_business_name_source"] = $"ATK validation ({AtkEnvironment})",
            }, actor);
        TempData["Message"] = switching
            ? $"Taxpayer changed to {info.Name} (NUI {info.Nui}, unit {info.BranchId}). Receipts already issued stay under the previous taxpayer. " +
              (Workstations > 0 ? $"Register the {Workstations} workstation(s) with ATK again under Settings › Workstations." : "Add the workstations under Settings › Workstations.")
            : Current is null ? "Business saved. Next: add the workstations under Settings › Workstations." : "Business details saved and logged.";
        return RedirectToPage();
    }

    /// <summary>Takes over the business name exactly as ATK has it (from the last registration or certificate).</summary>
    public async Task<IActionResult> OnPostUseAtkNameAsync()
    {
        await LoadAsync();
        if (Current is null || AtkName is null)
        {
            TempData["Error"] = "There is no name from ATK yet. Validate the business with ATK or register a workstation first.";
            return RedirectToPage();
        }
        Current.Name = AtkName;
        await settings.SaveBusinessAsync(Current, User.Identity!.Name!);
        TempData["Message"] = $"Business name changed to “{AtkName}”, as registered with ATK. The change is logged.";
        return RedirectToPage();
    }

    /// <summary>Deletes all business data and settings so the application starts from zero (test data only).</summary>
    public async Task<IActionResult> OnPostResetAsync(string? confirmNui)
    {
        var b = await settings.BusinessAsync();
        if (b is null) return RedirectToPage();
        if (confirmNui?.Trim() != b.Nui.ToString())
        {
            TempData["Error"] = "The NUI you typed does not match. Nothing was deleted.";
            return RedirectToPage(new { Reset = true });
        }
        try
        {
            var deleted = await reset.ResetAsync(User.Identity!.Name!);
            TempData["Message"] = $"Started over: {deleted.Receipts} receipt(s), {deleted.Workstations} workstation(s) and all settings of {b.Name} were deleted. Set up the new business.";
            return RedirectToPage(new { Edit = true });
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
    }
}
