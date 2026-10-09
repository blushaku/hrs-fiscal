using System.ComponentModel.DataAnnotations;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Signing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Settings;

public sealed class BusinessModel(SettingsStore settings) : PageModel
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
    }

    [BindProperty] public Form Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public bool Edit { get; set; }
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
    public bool Editing => Edit || !ModelState.IsValid;

    public async Task OnGetAsync()
    {
        var b = Current = await settings.BusinessAsync();
        await LoadAtkNameAsync();
        Locked = b is not null;
        if (b is not null)
            Input = new Form
            {
                Nui = b.Nui, Name = b.Name, FiscalizationNo = b.FiscalizationNo, VatNo = b.VatNo, BranchId = b.BranchId,
                BranchName = b.BranchName, Location = b.Location, Address = b.Address, OperaHotelCode = b.OperaHotelCode,
            };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Current = await settings.BusinessAsync();
        await LoadAtkNameAsync();
        Locked = Current is not null;
        Edit = true;
        if (!ModelState.IsValid) return Page();
        try
        {
            await settings.SaveBusinessAsync(new BusinessInfo
            {
                Nui = Input.Nui, Name = Input.Name.Trim(), FiscalizationNo = Input.FiscalizationNo.Trim(), VatNo = Input.VatNo?.Trim(),
                BranchId = Input.BranchId, BranchName = Input.BranchName.Trim(), Location = Input.Location.Trim(),
                Address = Input.Address?.Trim(), OperaHotelCode = Input.OperaHotelCode?.Trim(),
            }, User.Identity!.Name!);
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
}
