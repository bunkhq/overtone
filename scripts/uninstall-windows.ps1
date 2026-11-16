<#
.SYNOPSIS
    Removes a per-user Overtone install (see install-windows.ps1).

.DESCRIPTION
    Installed as uninstall.ps1 beside Overtone.exe and run from Settings > Apps.
    Stops Overtone, removes the startup entry, Start menu shortcut, Installed apps
    entry and program files. Settings in %LOCALAPPDATA%\Overtone are kept unless
    -RemoveSettings is passed.
#>
param([switch]$RemoveSettings)

$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Overtone'

# Running from inside the folder being deleted: rerun from a temp copy.
if ($PSCommandPath -and $PSCommandPath.StartsWith($dest, [StringComparison]::OrdinalIgnoreCase)) {
    $copy = Join-Path $env:TEMP 'overtone-uninstall.ps1'
    Copy-Item $PSCommandPath $copy -Force
    $forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', $copy)
    if ($RemoveSettings) { $forward += '-RemoveSettings' }
    Start-Process powershell.exe -ArgumentList $forward
    exit 0
}

Get-Process Overtone -ErrorAction SilentlyContinue | ForEach-Object {
    $_ | Stop-Process -Force
    $_.WaitForExit(5000) | Out-Null
}

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Overtone' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -Name 'Overtone' -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Overtone.lnk') -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Overtone' -Recurse -ErrorAction SilentlyContinue
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
if ($RemoveSettings) { Remove-Item (Join-Path $env:LOCALAPPDATA 'Overtone') -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host 'Overtone uninstalled.'
