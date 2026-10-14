using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Hrs.Fiscal.Server.Data;

namespace Hrs.Fiscal.Server.Services;

/// <summary>
/// The Fiscal Client installation package for one workstation: the standard client program (folder "client" next to the
/// server), the install script and pairing.json with the server address and this workstation's one-time pairing code.
/// </summary>
public sealed class ClientPackage(IWebHostEnvironment env, IConfiguration config)
{
    public string Folder => config["FiscalClient:PackageFolder"] is { Length: > 0 } f ? f : Path.Combine(env.ContentRootPath, "client");

    /// <summary>The client program is part of this installation (release packages include it).</summary>
    public bool Available => File.Exists(Path.Combine(Folder, "Hrs.Fiscal.Client.exe")) || File.Exists(Path.Combine(Folder, "Hrs.Fiscal.Client.dll"));

    public byte[] Build(TerminalEdit terminal, string serverUrl, string code, DateTime expiresUtc)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Folder, file).Replace('\\', '/');
                if (relative.Equals("install-client.ps1", StringComparison.OrdinalIgnoreCase)) continue;
                zip.CreateEntryFromFile(file, "Client/" + relative, CompressionLevel.Optimal);
            }
            var script = Path.Combine(Folder, "install-client.ps1");
            if (File.Exists(script)) zip.CreateEntryFromFile(script, "install-client.ps1");
            Add(zip, "pairing.json", JsonSerializer.Serialize(new { serverUrl, code, posId = terminal.PosId, expiresUtc },
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            Add(zip, "README.txt", $"""
                Opera Cloud Fiscal Solution - Kosovo — Fiscal Client for workstation POS {terminal.PosId} ({terminal.Hostname})

                1. Copy this folder to {terminal.Hostname} and open PowerShell there as administrator.
                2. Run:  powershell -ExecutionPolicy Bypass -File .\install-client.ps1
                3. Within a minute the client pairs with the fiscal server ({serverUrl}), creates this workstation's key
                   and registers it with ATK. Check it on the server under Settings › Workstations, or on this PC at
                   http://localhost:5110

                The pairing code in pairing.json works once, for this workstation only, until {expiresUtc:yyyy-MM-dd HH:mm} UTC.

                ---
                Kopjojeni këtë dosje në {terminal.Hostname}, hapni PowerShell si administrator dhe ekzekutoni install-client.ps1.
                Brenda një minute klienti lidhet me serverin fiskal, krijon çelësin e vendpunësit dhe regjistrohet në ATK.
                """);
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
