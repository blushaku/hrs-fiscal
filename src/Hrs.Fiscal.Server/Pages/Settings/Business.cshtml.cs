using System.ComponentModel.DataAnnotations;
using Hrs.Fiscal.Server.Data;
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
    public bool Locked { get; private set; }

    public async Task OnGetAsync()
    {
        var b = await settings.BusinessAsync();
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
        Locked = await settings.BusinessAsync() is not null;
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
        return RedirectToPage();
    }
}
