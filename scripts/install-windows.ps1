<#
.SYNOPSIS
    Builds Overtone and installs it for the current user: no admin rights, no MSIX.

.DESCRIPTION
    Publishes a self-contained Release build (with the Rust core), then:
      - copies it to %LOCALAPPDATA%\Programs\Overtone (replacing an earlier install),
      - adds a Start menu shortcut,
      - starts it at sign-in (the tray menu's "Start with Windows" turns this off),
      - lists it in Settings > Apps > Installed apps, with an uninstaller,
      - launches it.
    Run it again to update. Settings in %LOCALAPPDATA%\Overtone are kept.

.PARAMETER NoStartup
    Don't start Overtone at sign-in.

.PARAMETER NoLaunch
    Don't launch Overtone after installing.
#>
param(
    [switch]$NoStartup,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'apps\windows\Overtone\Overtone.csproj'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Overtone'
$exe = Join-Path $dest 'Overtone.exe'

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$platform = if ($arch -eq 'arm64') { 'ARM64' } else { 'x64' }
$rid = "win-$arch"
$publish = Join-Path $repo "apps\windows\Overtone\bin\publish\$rid"

Write-Host "Building Overtone ($rid)..."
dotnet publish $project -c Release -r $rid -p:Platform=$platform --self-contained -o $publish -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
if (-not (Test-Path (Join-Path $publish 'overtone_core.dll'))) { throw 'overtone_core.dll missing from the publish output' }

# Close a running copy so its files can be replaced. It has no window to close, so stop it.
Get-Process Overtone -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Closing running Overtone (pid $($_.Id))..."
    $_ | Stop-Process -Force
    $_.WaitForExit(5000) | Out-Null
}

Write-Host "Installing to $dest..."
New-Item -ItemType Directory -Force $dest | Out-Null
# Mirror: files dropped from a newer build don't linger from an older one.
robocopy $publish $dest /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
Copy-Item (Join-Path $PSScriptRoot 'uninstall-windows.ps1') (Join-Path $dest 'uninstall.ps1') -Force

# Start menu shortcut.
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Overtone.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $dest
$shortcut.IconLocation = "$exe,0"
$shortcut.Description = 'Now playing and synced lyrics, floating over your desktop'
$shortcut.Save()

# Start at sign-in: the same entry the app's tray toggle and Task Manager manage.
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$approved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
if ($NoStartup) {
    Remove-ItemProperty -Path $run -Name 'Overtone' -ErrorAction SilentlyContinue
} else {
    Set-ItemProperty -Path $run -Name 'Overtone' -Value "`"$exe`" --startup"
    Remove-ItemProperty -Path $approved -Name 'Overtone' -ErrorAction SilentlyContinue
}

# Installed apps entry.
[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
$sizeKb = [int]((Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Overtone'
New-Item -Path $uninstallKey -Force | Out-Null
$uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$dest\uninstall.ps1`""
@{
    DisplayName     = 'Overtone'
    DisplayVersion  = "$version"
    Publisher       = 'BUNK'
    DisplayIcon     = "$exe,0"
    InstallLocation = $dest
    UninstallString = $uninstallCmd
    QuietUninstallString = $uninstallCmd
}.GetEnumerator() | ForEach-Object { Set-ItemProperty -Path $uninstallKey -Name $_.Key -Value $_.Value }
Set-ItemProperty -Path $uninstallKey -Name 'EstimatedSize' -Value $sizeKb -Type DWord
Set-ItemProperty -Path $uninstallKey -Name 'NoModify' -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name 'NoRepair' -Value 1 -Type DWord

if (-not $NoLaunch) { Start-Process $exe -WorkingDirectory $dest }

Write-Host "Overtone $version installed." -ForegroundColor Green
if (-not $NoStartup) { Write-Host 'It starts when you sign in; turn that off from the tray menu.' }
