namespace Hrs.Fiscal.Server.Printing;

/// <summary>A receipt printer model: paper width, characters per line in the normal font and QR module size.</summary>
public sealed record PrinterModel(string Key, string Label, int PaperMm, int Columns, int QrModule);

public static class PrinterModels
{
    /// <summary>Epson TM-T88 (VII, VI, V) prints at 180 dpi: 42 characters (font A) on 80 mm paper, 30 on 58 mm.
    /// Most other receipt printers print at 203 dpi: 48 and 32.</summary>
    public static readonly IReadOnlyList<PrinterModel> All =
    [
        new("epson-tm-t88-80", "Epson TM-T88 (VII, VI, V), 80 mm", 80, 42, 5),
        new("epson-tm-t88-58", "Epson TM-T88 (VII, VI, V), 58 mm", 58, 30, 4),
        new("generic-80", "Other ESC/POS printer, 80 mm", 80, 48, 5),
        new("generic-58", "Other ESC/POS printer, 58 mm", 58, 32, 4),
    ];

    public const string Default = "epson-tm-t88-80";

    public static PrinterModel Get(string? key) => All.FirstOrDefault(m => m.Key == key) ?? All[0];
}

/// <summary>How a workstation's printer is connected.</summary>
public static class PrinterKinds
{
    public const string None = "none", Network = "network", Usb = "usb", Serial = "serial";
    public static readonly string[] All = [None, Network, Usb, Serial];
}

/// <summary>Serial (COM) port settings. Epson TM printers leave the factory at 38400 bps, 8 data bits, no parity,
/// 1 stop bit and DTR/DSR flow control (check the printer's self-test page).</summary>
public static class SerialSettings
{
    public static readonly int[] BaudRates = [9600, 19200, 38400, 57600, 115200];
    public const string FlowHardware = "hardware", FlowXonXoff = "xonxoff", FlowNone = "none";
    public static readonly string[] Flows = [FlowHardware, FlowXonXoff, FlowNone];
}
