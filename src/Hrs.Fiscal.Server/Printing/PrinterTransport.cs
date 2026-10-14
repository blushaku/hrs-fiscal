using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Hrs.Fiscal.Server.Printing;

/// <summary>Sends a finished print job to a printer.</summary>
public interface IPrinterTransport
{
    /// <param name="kind">"network" (address = host or host:port, port 9100 by default) or "windows" (address = printer name).</param>
    Task SendAsync(string kind, string address, byte[] job, CancellationToken ct = default);
}

/// <summary>
/// Network printers get the job over a raw TCP connection (port 9100, "JetDirect"), which every network receipt printer
/// accepts. Windows printers get it through the print spooler as RAW data, so the printer driver passes the ESC/POS
/// commands through unchanged. The Windows service runs as Local System: the printer must be installed on the fiscal
/// server for all users (a printer added only in one user's profile is not visible to the service).
/// </summary>
public sealed class PrinterTransport : IPrinterTransport
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public Task SendAsync(string kind, string address, byte[] job, CancellationToken ct = default) => kind switch
    {
        "network" => SendNetworkAsync(address, job, ct),
        "windows" => Task.Run(() => SendWindows(address, job), ct),
        _ => throw new PrinterException("No printer is set up for this workstation."),
    };

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

    private static void SendWindows(string printerName, byte[] job)
    {
        if (!OperatingSystem.IsWindows())
            throw new PrinterException("Windows printers can be used only when the fiscal server runs on Windows.");
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
