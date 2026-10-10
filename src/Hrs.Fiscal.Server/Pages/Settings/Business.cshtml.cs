using System.ComponentModel.DataAnnotations;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Signing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Settings;

public sealed class BusinessModel(SettingsStore settings, BusinessReset reset) : PageModel
{
    public sealed class Form
    {
        [Required, Range(1, long.MaxValue, ErrorMessage = "NUI is required.")] public long Nui { get; set; }
        [Required] public string Name { get; set; } = "";
        [Required] public string FiscalizationNo { get; set; } = "";
        public string? VatNo { get; set; }
        [Required, Range(1, 899_999_999, ErrorMessage = "Unit number must be between 1 and 899999999.")] public long BranchId { get; set; }
        [Required] public string BranchName { get; set; } = "";
        [Required] public string Location { get; set; } = "";
        public string? Address { get; set; }
        public string? OperaHotelCode { get; set; }
        /// <summary>Issued by ATK together with the registration (setting atk_application_id).</summary>
        [Range(0, long.MaxValue, ErrorMessage = "Application ID must be a number.")] public long ApplicationId { get; set; }
    }

    [BindProperty] public Form Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public bool Edit { get; set; }
    [BindProperty(SupportsGet = true)] public bool Reset { get; set; }
    public long ApplicationId { get; private set; }
    public int Workstations { get; private set; }
    public long WaitingToSend { get; private set; }
    public long Receipts { get; private set; }
    /// <summary>Ever connected to ATK Production: then the data are legal records and cannot be wiped here.</summary>
    public bool ProductionUsed { get; private set; }
    public bool Locked { get; private set; }
    public BusinessInfo? Current { get; private set; }
    /// <summary>Business name ATK returned at the last workstation registration (or in an imported ATK certificate).</summary>
    public string? AtkName { get; private set; }
    public string? AtkNameSource { get; private set; }
    public bool NameMatchesAtk => AtkName is null || Current is null || TerminalEnrollment.SameName(AtkName, Current.Name);

    private async Task LoadAtkNameAsync()
    {
        AtkName = await settings.GetAsync<string>("atk_business_name", null);
        if (string.IsNullOrWhiteSpace(AtkName)) AtkName = null;
        AtkNameSource = await settings.GetAsync<string>("atk_business_name_source", null);
    }
    /// <summary>The editor opens by itself when requested (?Edit=true) or after a failed save.</summary>
    public bool Editing => (Edit || !ModelState.IsValid) && !Reset;

    private async Task LoadExtrasAsync()
    {
        ApplicationId = await settings.GetAsync<long>("atk_application_id", 0);
        Workstations = (await settings.TerminalsAsync()).Count;
        WaitingToSend = await settings.WaitingToSendAsync();
        (Receipts, ProductionUsed) = await reset.StatusAsync();
    }

    public async Task OnGetAsync()
    {
        var b = Current = await settings.BusinessAsync();
        await LoadAtkNameAsync();
        await LoadExtrasAsync();
        Locked = b is not null;
        if (b is not null)
            Input = new Form
            {
                Nui = b.Nui, Name = b.Name, FiscalizationNo = b.FiscalizationNo, VatNo = b.VatNo, BranchId = b.BranchId,
                BranchName = b.BranchName, Location = b.Location, Address = b.Address, OperaHotelCode = b.OperaHotelCode,
                ApplicationId = ApplicationId,
            };
        else Input.ApplicationId = ApplicationId;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Current = await settings.BusinessAsync();
        await LoadAtkNameAsync();
        await LoadExtrasAsync();
        Locked = Current is not null;
        Edit = true;
        if (!ModelState.IsValid) return Page();
        try
        {
            var info = new BusinessInfo
            {
                Nui = Input.Nui, Name = Input.Name.Trim(), FiscalizationNo = Input.FiscalizationNo.Trim(), VatNo = Input.VatNo?.Trim(),
                BranchId = Input.BranchId, BranchName = Input.BranchName.Trim(), Location = Input.Location.Trim(),
                Address = Input.Address?.Trim(), OperaHotelCode = Input.OperaHotelCode?.Trim(),
            };
            var switching = Current is not null && (Current.Nui != info.Nui || Current.BranchId != info.BranchId);
            if (switching) await settings.ChangeTaxpayerAsync(info, User.Identity!.Name!);
            else await settings.SaveBusinessAsync(info, User.Identity!.Name!);
            await settings.SetManyAsync(new Dictionary<string, object?> { ["atk_application_id"] = Input.ApplicationId }, User.Identity!.Name!);
            if (switching)
            {
                TempData["Message"] = $"Taxpayer changed to {info.Name} (NUI {info.Nui}, unit {info.BranchId}). Receipts already issued stay under the previous taxpayer. " +
                                      (Workstations > 0 ? $"Register the {Workstations} workstation(s) with ATK again under Settings › Workstations." : "Add the workstations under Settings › Workstations.");
                return RedirectToPage();
            }
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return Page();
        }
        TempData["Message"] = "Business details saved and logged.";
        return RedirectToPage(new { Edit = false });
    }

    /// <summary>Takes over the business name exactly as ATK has it (from the last registration or certificate).</summary>
    public async Task<IActionResult> OnPostUseAtkNameAsync()
    {
        var b = await settings.BusinessAsync();
        await LoadAtkNameAsync();
        if (b is null || AtkName is null)
        {
            TempData["Error"] = "There is no name from ATK yet. Register a workstation with ATK first.";
            return RedirectToPage();
        }
        b.Name = AtkName;
        await settings.SaveBusinessAsync(b, User.Identity!.Name!);
        TempData["Message"] = $"Business name changed to \u201c{AtkName}\u201d, as registered with ATK. The change is logged.";
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
