using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Printing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Settings;

/// <summary>
/// Where the guest's fiscal receipt is printed: in the fiscal block of the OPERA folio, or by the fiscal server on a thermal
/// printer (58–100 mm, Doc. 01-06-1967 Art 26.14). Each workstation has its own printer: network (IP:port) or Windows.
/// </summary>
public sealed class PrintingModel(SettingsStore settings, ReceiptPrinting printing) : PageModel
{
    public sealed class PrinterForm
    {
        public string Kind { get; set; } = "network";
        public string? Address { get; set; }
        public short PaperMm { get; set; } = 80;
        public bool Auto { get; set; } = true;
    }

    public string Mode { get; private set; } = "folio";
    public IReadOnlyList<TerminalEdit> Terminals { get; private set; } = [];
    [BindProperty] public string? NewMode { get; set; }
    [BindProperty] public PrinterForm Printer { get; set; } = new();
    /// <summary>Workstation whose printer is being set up (?Edit=id).</summary>
    [BindProperty(SupportsGet = true)] public long? Edit { get; set; }
    /// <summary>?Mode=edit opens the "where is the receipt printed" editor.</summary>
    [BindProperty(SupportsGet = true, Name = "Change")] public bool ChangeMode { get; set; }
    public TerminalEdit? Current => Edit is { } id ? Terminals.SingleOrDefault(t => t.Id == id) : null;

    public async Task OnGetAsync()
    {
        await LoadAsync();
        if (Current is { } t)
            Printer = new PrinterForm { Kind = t.PrinterKind == "none" ? "network" : t.PrinterKind, Address = t.PrinterAddress, PaperMm = t.PrinterPaperMm, Auto = t.PrinterAuto };
    }

    public async Task<IActionResult> OnPostModeAsync()
    {
        if (NewMode is not ("folio" or "printer")) return BadRequest();
        await settings.SetManyAsync(new Dictionary<string, object?> { ["receipt_print_mode"] = NewMode }, User.Identity!.Name!);
        TempData["Message"] = NewMode == "printer"
            ? "Fiscal receipts are now printed on the workstations' thermal printers. Set up a printer for each workstation below."
            : "Fiscal receipts are now printed in the fiscal block of the OPERA folio.";
        return RedirectToPage(new { Edit = (long?)null, Change = (bool?)null });
    }

    public async Task<IActionResult> OnPostPrinterAsync()
    {
        await LoadAsync();
        if (Current is not { } t) return NotFound();
        var errors = new List<string>();
        Printer.Address = Printer.Address?.Trim();
        if (Printer.Kind is not ("none" or "network" or "windows")) errors.Add("Choose how the printer is connected.");
        if (Printer.Kind != "none" && string.IsNullOrWhiteSpace(Printer.Address))
            errors.Add(Printer.Kind == "network" ? "Enter the printer's IP address (and port, if not 9100)." : "Enter the name of the printer as it is installed in Windows.");
        if (Printer.Kind == "network" && !string.IsNullOrWhiteSpace(Printer.Address))
        {
            try { PrinterTransport.ParseAddress(Printer.Address); } catch (PrinterException ex) { errors.Add(ex.Message); }
        }
        if (Printer.PaperMm is not (58 or 80)) errors.Add("Paper width is 58 or 80 mm.");
        if (errors.Count > 0)
        {
            foreach (var e in errors) ModelState.AddModelError("", e);
            return Page();
        }
        await settings.SaveTerminalPrinterAsync(t.Id, Printer.Kind, Printer.Address, Printer.PaperMm, Printer.Auto, User.Identity!.Name!);
        TempData["Message"] = Printer.Kind == "none"
            ? $"Printer removed from workstation POS {t.PosId}."
            : $"Printer for workstation POS {t.PosId} saved. Use Test print to check it.";
        return RedirectToPage(new { Edit = (long?)null });
    }

    public async Task<IActionResult> OnPostTestAsync(long id)
    {
        var outcome = await printing.TestPrintAsync(id, User.Identity!.Name!);
        TempData[outcome.Ok ? "Message" : "Error"] = outcome.Message;
        return RedirectToPage(new { Edit = (long?)null });
    }

    private async Task LoadAsync()
    {
        Mode = await settings.ReceiptPrintModeAsync();
        Terminals = (await settings.TerminalsAsync()).Where(t => t.Status != "disabled").ToList();
    }
}
