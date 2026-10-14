namespace Hrs.Fiscal.Core.Printing;

/// <summary>How a workstation's printer is connected.</summary>
public static class PrinterKinds
{
    public const string None = "none", Network = "network", Usb = "usb", Serial = "serial";
    /// <summary>Printer connected to the front-desk PC by USB or COM port; the Fiscal Client on that PC prints.</summary>
    public const string ClientUsb = "client-usb", ClientSerial = "client-serial";
    public static readonly string[] All = [None, Network, Usb, Serial, ClientUsb, ClientSerial];
    public static bool OnClient(string kind) => kind is ClientUsb or ClientSerial;
    /// <summary>The local connection the Fiscal Client uses for a client-* kind ("usb" or "serial").</summary>
    public static string Local(string kind) => kind switch { ClientUsb => Usb, ClientSerial => Serial, _ => kind };
}

/// <summary>Serial (COM) port settings. Epson TM printers leave the factory at 38400 bps, 8 data bits, no parity,
/// 1 stop bit and DTR/DSR flow control (check the printer's self-test page).</summary>
public static class SerialSettings
{
    public static readonly int[] BaudRates = [9600, 19200, 38400, 57600, 115200];
    public const string FlowHardware = "hardware", FlowXonXoff = "xonxoff", FlowNone = "none";
    public static readonly string[] Flows = [FlowHardware, FlowXonXoff, FlowNone];
}
