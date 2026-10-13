#requires -Version 5.1
# Usage: .\MERGE_PRIVATE_BACKUPS.ps1 -PublicZip .\MS002_Portable_GitHub_Backup_20261013.zip -WorkerJs .\worker.js -LicenseSql .\license.sql -WindowsDataZip .\SignalSchedulerData.zip
# Never upload resulting private ZIP to a public GitHub repo.
param(
 [Parameter(Mandatory=$true)][string]$PublicZip,
 [string]$WorkerJs,
 [string]$LicenseSql,
 [string]$WindowsDataZip,
 [string]$VpsBackup,
 [string]$OutputZip="MS002_COMPLETE_PRIVATE_20261013.zip"
)
$ErrorActionPreference="Stop"
$root=Join-Path $env:TEMP ("MS002_Recovery_"+[guid]::NewGuid().ToString('N'))
New-Item -Type Directory -Path $root -Force | Out-Null
try {
  $src=(Resolve-Path -LiteralPath $PublicZip).Path
  Expand-Archive -LiteralPath $src -DestinationPath $root -Force
  $private=Join-Path $root "cloudflare-private"
  New-Item -Type Directory -Path $private -Force | Out-Null
  $payload=@{
    WorkerJs="signal-scheduler-license.js"
    LicenseSql="signal_scheduler_license.sql"
    WindowsDataZip="SignalSchedulerData.zip"
    VpsBackup="vps-backup"
  }
  foreach($k in $payload.Keys) {
    $arg=(Get-Variable -Name $k -ValueOnly)
    if(-not [string]::IsNullOrWhiteSpace($arg)) {
      $full=(Resolve-Path -LiteralPath $arg).Path
      Copy-Item -LiteralPath $full -Destination (Join-Path $private $payload[$k]) -Force -Recurse
    }
  }
  $manifest = @{
    "created_utc" = [DateTime]::UtcNow.ToString("o")
    "worker_supplied" = [bool]$WorkerJs
    "d1_sql_supplied" = [bool]$LicenseSql
    "local_data_supplied" = [bool]$WindowsDataZip
    "vps_supplied" = [bool]$VpsBackup
    "warning"="PRIVATE BACKUP: encrypt before online storage."
  }
  $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $private "LOCAL_MERGE_MANIFEST.json") -Encoding UTF8
  $out=[IO.Path]::GetFullPath($OutputZip)
  if(Test-Path -LiteralPath $out){ throw "Target exists: $out" }
  Compress-Archive -Path (Join-Path $root "*") -DestinationPath $out -CompressionLevel Optimal
  Write-Output ("Private bundle created: "+$out)
  Write-Warning "Bundle is NOT encrypted! Store encrypted and offline."
} finally {
  Remove-Item -LiteralPath $root -Force -Recurse -ErrorAction SilentlyContinue
}