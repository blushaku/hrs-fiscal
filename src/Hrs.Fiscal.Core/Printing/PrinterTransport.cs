using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Ports;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Hrs.Fiscal.Core.Printing;

/// <summary>Where a print job goes: connection kind, address (IP[:port], Windows printer name or COM port) and the
/// serial settings used only for COM ports.</summary>
public sealed record PrinterTarget(string Kind, string Address, int Baud = 38400, string Flow = SerialSettings.FlowHardware)
{
}

/// <summary>Sends a finished print job to a printer.</summary>
public interface IPrinterTransport
{
    Task SendAsync(PrinterTarget target, byte[] job, CancellationToken ct = default);
    /// <summary>Printers installed in Windows on the fiscal server (for the USB connection); empty elsewhere.</summary>
    IReadOnlyList<string> InstalledPrinters();
    /// <summary>Serial ports of the fiscal server (COM1, COM3 …).</summary>
    IReadOnlyList<string> SerialPorts();
}

/// <summary>
/// <b>Network</b>: raw TCP to port 9100 (every network receipt printer, e.g. the Ethernet port of an Epson TM-T88VII).
/// <b>USB</b>: a printer installed in Windows on the fiscal server (Epson Advanced Printer Driver or the generic driver);
/// the job goes through the spooler as RAW data, so the ESC/POS commands pass unchanged. The service runs as Local System,
/// so install the printer for all users. A shared printer (\\PC\Name) can work if the server's computer account may use it.
/// <b>Serial</b>: a COM port on the fiscal server (RS-232, or USB through Epson's TM Virtual Port Driver), 8 data bits,
/// no parity, 1 stop bit; jobs to one port are sent one after the other.
/// USB and serial printers must be connected to the fiscal server itself: the front-desk PCs only run a browser.
/// </summary>
public sealed class PrinterTransport : IPrinterTransport
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PortLocks = new(StringComparer.OrdinalIgnoreCase);

    public Task SendAsync(PrinterTarget target, byte[] job, CancellationToken ct = default) => target.Kind switch
    {
        PrinterKinds.Network => SendNetworkAsync(target.Address, job, ct),
        PrinterKinds.Usb => Task.Run(() => SendWindows(target.Address, job), ct),
        PrinterKinds.Serial => SendSerialAsync(target, job, ct),
        _ => throw new PrinterException("No printer is set up for this workstation."),
    };

    public IReadOnlyList<string> InstalledPrinters() => OperatingSystem.IsWindows() ? EnumPrintersWindows() : [];

    public IReadOnlyList<string> SerialPorts()
    {
        try { return SerialPort.GetPortNames().Distinct().Order().ToList(); }
        catch { return []; }
    }

    public static (string Host, int Port) ParseAddress(string address)
    {
        var a = (address ?? "").Trim();
        if (a.Length == 0) throw new PrinterException("The printer address is empty.");
        var colon = a.LastIndexOf(':');
        if (colon > 0 && a.IndexOf(':') == colon && int.TryParse(a[(colon + 1)..], out var port))
        {
            if (port is < 1 or > 65535) throw new PrinterException($"Port {port} is not valid.");
            return (a[..colon], port);
        }
        return (a, 9100);
    }

    private static async Task SendNetworkAsync(string address, byte[] job, CancellationToken ct)
    {
        var (host, port) = ParseAddress(address);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(job, timeout.Token);
            await stream.FlushAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PrinterException($"The printer at {host}:{port} did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (SocketException ex)
        {
            throw new PrinterException($"Cannot reach the printer at {host}:{port}: {ex.Message}");
        }
    }

    private static async Task SendSerialAsync(PrinterTarget target, byte[] job, CancellationToken ct)
    {
        var portName = target.Address.Trim();
        if (portName.Length == 0) throw new PrinterException("Enter the COM port of the printer.");
        var gate = PortLocks.GetOrAdd(portName, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(Timeout, ct)) throw new PrinterException($"Port {portName} is busy with another print job.");
        try
        {
            await Task.Run(() =>
            {
                using var port = new SerialPort(portName, target.Baud, Parity.None, 8, StopBits.One)
                {
                    WriteTimeout = (int)Timeout.TotalMilliseconds,
                    Handshake = target.Flow switch
                    {
                        SerialSettings.FlowHardware => Handshake.RequestToSend,
                        SerialSettings.FlowXonXoff => Handshake.XOnXOff,
                        _ => Handshake.None,
                    },
                    // DTR/DSR printers (Epson default): the printer's DTR line reaches the PC's CTS/DSR in Epson's cables.
                    DtrEnable = true,
                };
                try
                {
                    port.Open();
                    for (var offset = 0; offset < job.Length; offset += 1024)
                        port.Write(job, offset, Math.Min(1024, job.Length - offset));
                    var until = DateTime.UtcNow + Timeout;
                    while (port.BytesToWrite > 0 && DateTime.UtcNow < until) Thread.Sleep(20);
                    if (port.BytesToWrite > 0) throw new PrinterException($"The printer on {portName} did not take the data (offline, out of paper or wrong flow control).");
                }
                catch (TimeoutException) { throw new PrinterException($"The printer on {portName} did not take the data within {Timeout.TotalSeconds:0} seconds (offline, out of paper, or wrong speed or flow control)."); }
                catch (UnauthorizedAccessException) { throw new PrinterException($"Port {portName} is used by another program."); }
                catch (IOException ex) { throw new PrinterException($"Cannot open port {portName}: {ex.Message}"); }
                catch (ArgumentException) { throw new PrinterException($"Port {portName} does not exist on this server."); }
            }, ct);
        }
        finally { gate.Release(); }
    }

    private static void SendWindows(string printerName, byte[] job)
    {
        if (!OperatingSystem.IsWindows())
            throw new PrinterException("USB printers installed in Windows can be used only when the fiscal server runs on Windows.");
        if (!OpenPrinter(printerName.Trim(), out var handle, IntPtr.Zero))
            throw new PrinterException($"Windows printer \"{printerName}\" was not found: {new Win32Exception().Message}");
        try
        {
            var doc = new DocInfo1 { DocName = "Kupon fiskal", OutputFile = null, DataType = "RAW" };
            if (StartDocPrinter(handle, 1, ref doc) == 0) throw new PrinterException($"Printer \"{printerName}\": {new Win32Exception().Message}");
            try
            {
                StartPagePrinter(handle);
                if (!WritePrinter(handle, job, job.Length, out var written) || written != job.Length)
                    throw new PrinterException($"Printer \"{printerName}\" did not accept the data: {new Win32Exception().Message}");
                EndPagePrinter(handle);
            }
            finally { EndDocPrinter(handle); }
        }
        finally { ClosePrinter(handle); }
    }

    private static IReadOnlyList<string> EnumPrintersWindows()
    {
        const int Local = 0x2, Connections = 0x4;
        EnumPrinters(Local | Connections, null, 4, IntPtr.Zero, 0, out var needed, out _);
        if (needed <= 0) return [];
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumPrinters(Local | Connections, null, 4, buffer, needed, out _, out var count)) return [];
            var size = Marshal.SizeOf<PrinterInfo4>();
            var names = new List<string>();
            for (var i = 0; i < count; i++)
                if (Marshal.PtrToStructure<PrinterInfo4>(buffer + i * size).PrinterName is { } name) names.Add(name);
            return names.Order().ToList();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo4
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? PrinterName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ServerName;
        public uint Attributes;
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumPrinters(int flags, string? name, int level, IntPtr buffer, int size, out int needed, out int returned);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool ClosePrinter(IntPtr handle);
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr handle, int level, ref DocInfo1 docInfo);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndDocPrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool StartPagePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndPagePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool WritePrinter(IntPtr handle, byte[] bytes, int count, out int written);
}

public sealed class PrinterException(string message) : Exception(message);
