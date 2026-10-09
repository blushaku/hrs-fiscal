<#
  Installs HRS Fiscal Server as a Windows service.
  Run in an elevated PowerShell from the unzipped release folder:
    .\install-server.ps1 -ConnectionString "Host=localhost;Database=hrs_fiscal;Username=hrs_fiscal_owner;Password=..." `
                         -AdminPassword "first-admin-password" [-InstallDir "C:\Program Files\HRS Fiscal\Server"] `
                         [-AdminPort 5080] [-FlipPort 5100] [-FlipTcpPort 0] [-FlipSourceIp 192.168.1.10]
  Requires the .NET 8 ASP.NET Core Runtime (Windows Hosting Bundle) and PostgreSQL 14+ with an empty database.
  Migrations run on first start. Re-running the script upgrades in place: the service is replaced, the database is kept.
  Full manual: docs/manual-server-installation.md
#>
param(
  [Parameter(Mandatory)] [string] $ConnectionString,
  [string] $AdminPassword = "",
  [string] $InstallDir = "C:\Program Files\HRS Fiscal\Server",
  [int] $AdminPort = 5080,
  [int] $FlipPort = 5100,
  [int] $FlipTcpPort = 0,
  # Restrict the FLIP port to the FLIP machine (recommended). Empty = any address on the Domain/Private network.
  [string] $FlipSourceIp = "",
  # Where central-mode signing keys live: auto (Windows key store, TPM when present) | cng | file (test only).
  [string] $SigningKeyStore = "auto"
)
$ErrorActionPreference = "Stop"
$svc = "HRSFiscalServer"
# Event log source used by the service (errors, start-up, ATK problems).
if (-not [System.Diagnostics.EventLog]::SourceExists("Hrs.Fiscal.Server")) { New-EventLog -LogName Application -Source "Hrs.Fiscal.Server" }

if (Get-Service $svc -ErrorAction SilentlyContinue) { Stop-Service $svc; sc.exe delete $svc | Out-Null; Start-Sleep 2 }
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item -Recurse -Force (Join-Path $PSScriptRoot "*") $InstallDir -Exclude "install-server.ps1"

$settings = @{
  ConnectionStrings = @{ Fiscal = $ConnectionString }
  Property = @{ TimeZone = "Europe/Belgrade" }
  Database = @{ MigrateOnStartup = $true }
  Bootstrap = @{ AdminPassword = $AdminPassword }
  Flip = @{ TcpPort = $FlipTcpPort }
  Signing = @{ KeyStore = $SigningKeyStore; PreferTpm = $true }
  Kestrel = @{ Endpoints = @{
    Admin = @{ Url = "http://0.0.0.0:$AdminPort" }
    Flip  = @{ Url = "http://0.0.0.0:$FlipPort" }
  } }
  Logging = @{ LogLevel = @{ Default = "Information"; "Microsoft.AspNetCore" = "Warning" } }
  AllowedHosts = "*"
}
$settingsFile = Join-Path $InstallDir "appsettings.Production.json"
$settings | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 $settingsFile
# The settings file holds the database password: readable by Administrators and the service (SYSTEM) only.
icacls $settingsFile /inheritance:r /grant:r "*S-1-5-32-544:F" "*S-1-5-18:F" | Out-Null

New-Service -Name $svc -DisplayName "HRS Fiscal Server" -StartupType Automatic `
  -BinaryPathName "`"$(Join-Path $InstallDir 'Hrs.Fiscal.Server.exe')`" --contentRoot `"$InstallDir`" --environment Production" | Out-Null
# Restart automatically after a crash (after 10 s, 30 s, then every 60 s).
sc.exe failure $svc reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
New-NetFirewallRule -DisplayName "HRS Fiscal admin ($AdminPort)" -Direction Inbound -Protocol TCP -LocalPort $AdminPort -Profile Domain,Private -Action Allow -ErrorAction SilentlyContinue | Out-Null
$remote = if ($FlipSourceIp) { $FlipSourceIp } else { "Any" }
New-NetFirewallRule -DisplayName "HRS Fiscal FLIP ($FlipPort)" -Direction Inbound -Protocol TCP -LocalPort $FlipPort -RemoteAddress $remote -Profile Domain,Private -Action Allow -ErrorAction SilentlyContinue | Out-Null
if ($FlipTcpPort -gt 0) { New-NetFirewallRule -DisplayName "HRS Fiscal FLIP TCP ($FlipTcpPort)" -Direction Inbound -Protocol TCP -LocalPort $FlipTcpPort -RemoteAddress $remote -Profile Domain,Private -Action Allow -ErrorAction SilentlyContinue | Out-Null }
Start-Service $svc
Start-Sleep 3
try { Invoke-RestMethod "http://localhost:$AdminPort/health" | Out-Null; Write-Host "Health check OK." -ForegroundColor Green }
catch { Write-Host "Service started but /health did not answer. See Event Viewer > Windows Logs > Application (source Hrs.Fiscal.Server)." -ForegroundColor Red }

Write-Host "HRS Fiscal Server installed and started."
Write-Host "  Admin:  http://$(hostname):$AdminPort   (user 'admin')"
Write-Host "  FLIP:   http://<this server's LAN IP>:$FlipPort/flip"
if ($AdminPassword) { Write-Host "  Remove Bootstrap.AdminPassword from appsettings.Production.json after the first sign-in." -ForegroundColor Yellow }
