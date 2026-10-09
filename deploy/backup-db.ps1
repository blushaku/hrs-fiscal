<#
  Backs up the Opera Cloud Fiscal Solution database (all receipts, transmissions, OPERA/FLIP messages and the audit log).
  Schedule it daily with Task Scheduler (see docs/manual-server-installation.md, "Backup"):
    .\backup-db.ps1 -Target "\\nas\hrs-fiscal-backup" [-Database hrs_fiscal] [-User hrs_fiscal_owner] [-KeepDays 90]
  The password is read from %APPDATA%\postgresql\pgpass.conf of the account running the task (never from this script).
  Writes hrs_fiscal_<date>.dump (pg_dump custom format) plus a .sha256 file, verifies the dump is readable,
  and logs to the Windows Application event log. Only old BACKUP FILES are removed; the database itself is never touched.
#>
param(
  [Parameter(Mandatory)] [string] $Target,
  [string] $Database = "hrs_fiscal",
  [string] $User = "hrs_fiscal_owner",
  [string] $DbHost = "127.0.0.1",
  [int] $Port = 5432,
  [int] $KeepDays = 90,
  [string] $PgBin = ""
)
$ErrorActionPreference = "Stop"
$source = "Fiscal Solution Backup"
if (-not [System.Diagnostics.EventLog]::SourceExists($source)) { New-EventLog -LogName Application -Source $source }

if (-not $PgBin) { $PgBin = (Get-ChildItem "C:\Program Files\PostgreSQL\*\bin\pg_dump.exe" | Sort-Object FullName -Descending | Select-Object -First 1).DirectoryName }
$dump = Join-Path $Target ("{0}_{1:yyyyMMdd_HHmmss}.dump" -f $Database, (Get-Date))
try {
  New-Item -ItemType Directory -Force $Target | Out-Null
  & (Join-Path $PgBin "pg_dump.exe") -h $DbHost -p $Port -U $User -w -Fc -f $dump $Database
  if ($LASTEXITCODE -ne 0) { throw "pg_dump failed with exit code $LASTEXITCODE" }
  & (Join-Path $PgBin "pg_restore.exe") --list $dump | Out-Null          # the dump must be readable
  if ($LASTEXITCODE -ne 0) { throw "pg_restore could not read $dump" }
  $hash = (Get-FileHash -Algorithm SHA256 $dump).Hash
  "$hash  $(Split-Path $dump -Leaf)" | Set-Content -Encoding ASCII "$dump.sha256"
  Get-ChildItem $Target -Filter "$($Database)_*.dump*" | Where-Object LastWriteTime -lt (Get-Date).AddDays(-$KeepDays) | Remove-Item
  Write-EventLog -LogName Application -Source $source -EventId 1000 -EntryType Information -Message "Backup OK: $dump ($((Get-Item $dump).Length) bytes, SHA-256 $hash)"
}
catch {
  Write-EventLog -LogName Application -Source $source -EventId 1001 -EntryType Error -Message "Backup FAILED: $_"
  throw
}
