<#
  Installs HRS Fiscal Server as a Windows service.
  Run in an elevated PowerShell from the unzipped release folder:
    .\install-server.ps1 -ConnectionString "Host=localhost;Database=hrs_fiscal;Username=hrs_fiscal_owner;Password=..." `
                         -AdminPassword "first-admin-password" [-InstallDir "C:\Program Files\HRS Fiscal\Server"] `
                         [-AdminPort 5080] [-FlipPort 5100] [-FlipTcpPort 0]
  Requires the .NET 8 ASP.NET Core Runtime (Windows Hosting Bundle) and PostgreSQL 14+ with an empty database.
  Migrations run on first start.
#>
param(
  [Parameter(Mandatory)] [string] $ConnectionString,
  [string] $AdminPassword = "",
  [string] $InstallDir = "C:\Program Files\HRS Fiscal\Server",
  [int] $AdminPort = 5080,
  [int] $FlipPort = 5100,
  [int] $FlipTcpPort = 0
)
$ErrorActionPreference = "Stop"
$svc = "HRSFiscalServer"

if (Get-Service $svc -ErrorAction SilentlyContinue) { Stop-Service $svc; sc.exe delete $svc | Out-Null; Start-Sleep 2 }
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item -Recurse -Force (Join-Path $PSScriptRoot "*") $InstallDir -Exclude "install-server.ps1"

$settings = @{
  ConnectionStrings = @{ Fiscal = $ConnectionString }
  Property = @{ TimeZone = "Europe/Belgrade" }
  Database = @{ MigrateOnStartup = $true }
  Bootstrap = @{ AdminPassword = $AdminPassword }
  Flip = @{ TcpPort = $FlipTcpPort }
  Kestrel = @{ Endpoints = @{
    Admin = @{ Url = "http://0.0.0.0:$AdminPort" }
    Flip  = @{ Url = "http://0.0.0.0:$FlipPort" }
  } }
  Logging = @{ LogLevel = @{ Default = "Information"; "Microsoft.AspNetCore" = "Warning" } }
  AllowedHosts = "*"
}
$settings | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $InstallDir "appsettings.Production.json")

New-Service -Name $svc -DisplayName "HRS Fiscal Server" -StartupType Automatic `
  -BinaryPathName "`"$(Join-Path $InstallDir 'Hrs.Fiscal.Server.exe')`" --contentRoot `"$InstallDir`"" | Out-Null
New-NetFirewallRule -DisplayName "HRS Fiscal admin ($AdminPort)" -Direction Inbound -Protocol TCP -LocalPort $AdminPort -Profile Domain,Private -Action Allow -ErrorAction SilentlyContinue | Out-Null
New-NetFirewallRule -DisplayName "HRS Fiscal FLIP ($FlipPort)" -Direction Inbound -Protocol TCP -LocalPort $FlipPort -Profile Domain,Private -Action Allow -ErrorAction SilentlyContinue | Out-Null
if ($FlipTcpPort -gt 0) { New-NetFirewallRule -DisplayName "HRS Fiscal FLIP TCP ($FlipTcpPort)" -Direction Inbound -Protocol TCP -LocalPort $FlipTcpPort -Profile Domain,Private -Action Allow -ErrorAction SilentlyContinue | Out-Null }
Start-Service $svc

Write-Host "HRS Fiscal Server installed and started."
Write-Host "  Admin:  http://$(hostname):$AdminPort   (user 'admin')"
Write-Host "  FLIP:   http://<this server's LAN IP>:$FlipPort/flip"
if ($AdminPassword) { Write-Host "  Remove Bootstrap.AdminPassword from appsettings.Production.json after the first sign-in." -ForegroundColor Yellow }
