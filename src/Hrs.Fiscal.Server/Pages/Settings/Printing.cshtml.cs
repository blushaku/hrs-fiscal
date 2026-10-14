using System.Text.RegularExpressions;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Printing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Settings;

/// <summary>
/// Where the guest's fiscal receipt is printed: in the fiscal block of the OPERA folio, or by the fiscal server on a thermal
/// printer (58–100 mm, Doc. 01-06-1967 Art 26.14). Each workstation has its own printer, connected by network, USB or
/// serial (COM) port.
/// </summary>
public sealed class PrintingModel(SettingsStore settings, ReceiptPrinting printing, IPrinterTransport transport) : PageModel
{
    public sealed class PrinterForm
    {
        public string Kind { get; set; } = PrinterKinds.Network;
        /// <summary>IP[:port], Windows printer name or COM port, depending on Kind (only the visible field is posted).</summary>
        public string? Address { get; set; }
        public string Model { get; set; } = PrinterModels.Default;
        public int Baud { get; set; } = 38400;
        public string Flow { get; set; } = SerialSettings.FlowHardware;
        public bool Auto { get; set; } = true;
    }

    public string Mode { get; private set; } = "folio";
    public IReadOnlyList<TerminalEdit> Terminals { get; private set; } = [];
    public IReadOnlyList<string> InstalledPrinters { get; private set; } = [];
    public IReadOnlyList<string> SerialPorts { get; private set; } = [];
    [BindProperty] public string? NewMode { get; set; }
    [BindProperty] public PrinterForm Printer { get; set; } = new();
    /// <summary>Workstation whose printer is being set up (?Edit=id).</summary>
    [BindProperty(SupportsGet = true)] public long? Edit { get; set; }
    /// <summary>?Change=true opens the "where is the receipt printed" editor.</summary>
    [BindProperty(SupportsGet = true, Name = "Change")] public bool ChangeMode { get; set; }
    public TerminalEdit? Current => Edit is { } id ? Terminals.SingleOrDefault(t => t.Id == id) : null;

    public async Task OnGetAsync()
    {
        await LoadAsync();
        if (Current is { } t)
            Printer = new PrinterForm
            {
                Kind = t.PrinterKind == PrinterKinds.None ? PrinterKinds.Network : t.PrinterKind, Address = t.PrinterAddress,
                Model = t.PrinterModel, Baud = t.PrinterBaud, Flow = t.PrinterFlow, Auto = t.PrinterAuto,
            };
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
        if (!PrinterKinds.All.Contains(Printer.Kind)) errors.Add("Choose how the printer is connected.");
        if (Printer.Kind != PrinterKinds.None && string.IsNullOrWhiteSpace(Printer.Address))
            errors.Add(Printer.Kind switch
            {
                PrinterKinds.Network => "Enter the printer's IP address (and port, if not 9100).",
                PrinterKinds.Usb => "Choose the USB printer as it is installed in Windows on the fiscal server.",
                _ => "Choose the COM port the printer is connected to.",
            });
        if (Printer.Kind == PrinterKinds.Network && !string.IsNullOrWhiteSpace(Printer.Address))
        {
            try { PrinterTransport.ParseAddress(Printer.Address); } catch (PrinterException ex) { errors.Add(ex.Message); }
        }
        if (Printer.Kind == PrinterKinds.Serial && !string.IsNullOrWhiteSpace(Printer.Address) && !Regex.IsMatch(Printer.Address, @"^(COM\d{1,3}|/dev/[\w/.-]+)$", RegexOptions.IgnoreCase))
            errors.Add("A COM port is written like COM1 or COM3.");
        if (PrinterModels.All.All(m => m.Key != Printer.Model)) errors.Add("Choose the printer model.");
        if (Printer.Kind != PrinterKinds.Serial) { Printer.Baud = t.PrinterBaud; Printer.Flow = t.PrinterFlow; }   // keep the serial settings
        if (!SerialSettings.BaudRates.Contains(Printer.Baud)) errors.Add("Choose the serial speed.");
        if (!SerialSettings.Flows.Contains(Printer.Flow)) errors.Add("Choose the flow control.");
        if (errors.Count > 0)
        {
            foreach (var e in errors) ModelState.AddModelError("", e);
            return Page();
        }
        if (Printer.Kind == PrinterKinds.Serial) Printer.Address = Printer.Address!.ToUpperInvariant().StartsWith("COM") ? Printer.Address.ToUpperInvariant() : Printer.Address;
        await settings.SaveTerminalPrinterAsync(t.Id, new PrinterSetup(Printer.Kind, Printer.Address, Printer.Model, Printer.Baud, Printer.Flow, Printer.Auto), User.Identity!.Name!);
        TempData["Message"] = Printer.Kind == PrinterKinds.None
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
        InstalledPrinters = transport.InstalledPrinters();
        SerialPorts = transport.SerialPorts();
    }

    public static string ConnectionLabel(string kind) => kind switch
    {
        PrinterKinds.Network => "Network",
        PrinterKinds.Usb => "USB",
        PrinterKinds.Serial => "Serial (COM)",
        _ => "None",
    };

    public static string FlowLabel(string flow) => flow switch
    {
        SerialSettings.FlowHardware => "Hardware (DTR/DSR, RTS/CTS)",
        SerialSettings.FlowXonXoff => "XON/XOFF",
        _ => "None",
    };
}
