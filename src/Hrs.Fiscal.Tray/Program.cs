// Opera Cloud Fiscal Solution - Kosovo — Fiscal Client tray program.
// Runs in every user's session on a workstation with the Fiscal Client (started at logon). It reads the status of the
// local Fiscal Client service (http://localhost:5110/status) every 5 seconds and shows it as a tray icon. While ATK
// cannot be reached, or the workstation cannot reach the fiscal server or its own Fiscal Client, it opens a small
// window in the corner of the screen (Doc. 01-06-1967 Art 26.13: visual notice to the user while offline).

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Hrs.Fiscal.Tray;

internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    [STAThread]
    private static void Main(string[] args)
    {
        using var single = new Mutex(true, @"Local\OperaCloudFiscalKosovoTray", out var first);
        if (!first) return;   // already running in this session
        try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext(Port(args)));
    }

    /// <summary>--port N, else Client:Port from the client's appsettings.Production.json next to this program, else 5110.</summary>
    internal static int Port(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var p)) return p;
        try
        {
            var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.Production.json");
            if (File.Exists(file) && Regex.Match(File.ReadAllText(file), "\"Port\"\\s*:\\s*(\\d+)") is { Success: true } m) return int.Parse(m.Groups[1].Value);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return 5110;
    }
}
