<#
  Installs the Fiscal Client of Opera Cloud Fiscal Solution - Kosovo on a front-desk workstation.
  Use the package downloaded for THIS workstation from the fiscal server (Settings › Workstations › the workstation ›
  Fiscal Client › Download installation package): it contains pairing.json with a one-time code for this workstation.
  Run in an elevated PowerShell from the unzipped package:
    powershell -ExecutionPolicy Bypass -File .\install-client.ps1 [-Port 5110]
  Without the package (standard client from the release, folder "client"): give the server address and the pairing code
  shown on the server for this workstation:
    powershell -ExecutionPolicy Bypass -File .\install-client.ps1 -ServerUrl http://192.168.1.10:5080 -PairingCode ABCD-EFGH-JKLM
  Re-running it upgrades in place; the key and the registration are kept (they live in %ProgramData%).
#>
param(
  [string] $ServerUrl,
  [string] $PairingCode,
  [int] $Port = 5110,
  [string] $InstallDir = "C:\Program Files\Opera Cloud Fiscal Solution\Client"
)
$ErrorActionPreference = "Stop"
$svc = "OperaCloudFiscalKosovoClient"
$data = Join-Path $env:ProgramData "Opera Cloud Fiscal Solution\Client"

# The client needs the ASP.NET Core 8 runtime (x64), as the fiscal server does.
$runtimes = try { & dotnet --list-runtimes 2>$null } catch { @() }
if (-not ($runtimes | Where-Object { $_ -match '^Microsoft\.AspNetCore\.App 8\.' })) {
  throw "ASP.NET Core Runtime 8 (x64) is not installed on this PC. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 (ASP.NET Core Runtime, Windows x64 installer) and run this script again."
}

if (Get-Service $svc -ErrorAction SilentlyContinue) { Stop-Service $svc; sc.exe delete $svc | Out-Null; Start-Sleep 2 }
New-Item -ItemType Directory -Force $InstallDir, $data | Out-Null
# Package layout: Client\ next to this script; release layout: the script inside the client folder itself.
$source = if (Test-Path (Join-Path $PSScriptRoot "Client\Hrs.Fiscal.Client.exe")) { Join-Path $PSScriptRoot "Client" } else { $PSScriptRoot }
if (-not (Test-Path (Join-Path $source "Hrs.Fiscal.Client.exe"))) { throw "Hrs.Fiscal.Client.exe not found next to this script." }
if ((Resolve-Path $source).Path.TrimEnd('\') -ne $InstallDir.TrimEnd('\')) { Copy-Item -Recurse -Force (Join-Path $source "*") $InstallDir -Exclude "install-client.ps1","pairing.json" }

$pairing = Join-Path $PSScriptRoot "pairing.json"
if ($ServerUrl -and $PairingCode) {
  # Manual pairing: write the same pairing.json the package would contain.
  $pairing = Join-Path $env:TEMP "fiscal-client-pairing.json"
  @{ serverUrl = $ServerUrl.TrimEnd('/'); code = $PairingCode.Trim().ToUpperInvariant(); posId = 0; expiresUtc = (Get-Date).ToUniversalTime().AddDays(2).ToString("o") } |
    ConvertTo-Json | Set-Content -Encoding UTF8 $pairing
} elseif ($ServerUrl -or $PairingCode) { throw "Give both -ServerUrl and -PairingCode, or use the package downloaded for this workstation." }

$server = $null
if (Test-Path $pairing) {
  $server = (Get-Content $pairing -Raw | ConvertFrom-Json).serverUrl
  $state = Join-Path $data "state.json"
  # A new package pairs this PC again (new code): forget the old pairing, keep nothing secret from before.
  if (Test-Path $state) { Remove-Item -Force $state }
  Copy-Item -Force $pairing (Join-Path $data "pairing.json")
} elseif (Test-Path (Join-Path $InstallDir "appsettings.Production.json")) {
  $server = (Get-Content (Join-Path $InstallDir "appsettings.Production.json") -Raw | ConvertFrom-Json).Client.ServerUrl
}
if (-not $server) { throw "pairing.json is missing: download the installation package for this workstation from the fiscal server." }

@{ Client = @{ ServerUrl = $server; Port = $Port } } | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $InstallDir "appsettings.Production.json")
# The data folder holds the pairing secret: Administrators and the service (SYSTEM) only.
icacls $data /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" | Out-Null

New-Service -Name $svc -DisplayName "Opera Cloud Fiscal Solution - Kosovo - Client" -StartupType Automatic `
  -BinaryPathName "`"$(Join-Path $InstallDir 'Hrs.Fiscal.Client.exe')`" --environment Production" | Out-Null
sc.exe failure $svc reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null

# Only the fiscal server may call this client.
$serverHost = ([Uri]$server).Host
$ips = @(try { [System.Net.Dns]::GetHostAddresses($serverHost) | ForEach-Object { $_.IPAddressToString } } catch { $serverHost })
Remove-NetFirewallRule -DisplayName "Fiscal Client*" -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName "Fiscal Client ($Port)" -Direction Inbound -Protocol TCP -LocalPort $Port -RemoteAddress $ips `
  -Profile Domain,Private -Action Allow | Out-Null

Start-Service $svc
Write-Host "Fiscal Client installed. It pairs with $server and registers with ATK by itself within a minute."
Write-Host "Status on this computer: http://localhost:$Port   ·   On the server: Settings › Workstations."
