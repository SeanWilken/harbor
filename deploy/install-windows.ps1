# Install harbor-agent + harbor-tray for the current Windows user, from this repository checkout.
#
#   powershell -ExecutionPolicy Bypass -File deploy\install-windows.ps1 -Hub http://100.101.1.10:7420 -Code 7KQ4-M2XP
#
# Needs: .NET 10 SDK (to build), git (the agent shells out to it), Tailscale (logged in to the same
# tailnet as the hub). Installs into %LOCALAPPDATA%\harbor\bin, writes %USERPROFILE%\.config\harbor\config.json
# (kept if it exists), adds a Startup shortcut that runs the tray, which runs the agent hidden, then starts it.
# Re-run to update (or tray -> "Update harbor...", which runs deploy\update-windows.ps1). Uninstall: delete the "harbor" shortcuts in Startup and the Start menu, %LOCALAPPDATA%\harbor and the config.
[CmdletBinding()]
param(
    # The hub's tailnet address (Pending -> "pair a device" shows it). Optional on re-runs: the
    # existing config's agent.hub is used.
    [string] $Hub,
    # One-time code from the hub; omit to approve the machine in the hub's Pending tab instead.
    [string] $Code,
    [string] $Machine = $env:COMPUTERNAME.ToLowerInvariant(),
    # Folders to look for git repos in; only the ones that exist are used.
    [string[]] $Roots = @("$HOME\source", "$HOME\Documents", "$HOME\Developer", "$HOME\repos", "C:\src")
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $env:LOCALAPPDATA "harbor\bin"
$configDir = Join-Path $HOME ".config\harbor"
$config = Join-Path $configDir "config.json"
if (-not $Hub) {
    if (Test-Path $config) { $Hub = (Get-Content -Raw $config | ConvertFrom-Json).agent.hub }
    if (-not $Hub) { throw "-Hub is required on the first install, e.g. -Hub http://100.101.1.10:7420" }
}

function Need([string] $exe, [string] $hint) {
    if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) { throw "$exe not found: $hint" }
}
Need dotnet "install the .NET 10 SDK (winget install Microsoft.DotNet.SDK.10)"
Need git "install Git for Windows (winget install Git.Git)"
if (-not (Get-Command tailscale -ErrorAction SilentlyContinue)) {
    Write-Warning "tailscale CLI not found; the agent needs this machine on the hub's tailnet"
}

Write-Host "Stopping a running tray/agent (if any)..."
Get-Process harbor-tray, harbor-agent -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Building into $bin ..."
New-Item -ItemType Directory -Force -Path $bin | Out-Null
dotnet publish (Join-Path $repo "src\Harbor.Agent") -c Release -r win-x64 -o $bin --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "building harbor-agent failed" }
dotnet publish (Join-Path $repo "src\Harbor.Tray") -c Release -r win-x64 -o $bin --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "building harbor-tray failed" }

if (Test-Path $config) {
    Write-Host "Keeping existing config $config"
} else {
    New-Item -ItemType Directory -Force -Path $configDir | Out-Null
    $found = @($Roots | Where-Object { Test-Path $_ })
    if ($found.Count -eq 0) { Write-Warning "none of the roots exist ($($Roots -join ', ')); edit roots in $config" }
    $cfg = [ordered]@{
        machine = $Machine
        roots   = $found
        # AppData is not a dot-folder on Windows, so skip it (and other caches) explicitly
        ignore  = @("node_modules", "AppData", ".cache", "bin", "obj", "dist", "target", "packages", ".nuget")
        services = @(@{ name = "tailscale"; check = @{ kind = "process"; name = "tailscaled" } })
        agent   = [ordered]@{ hub = $Hub.TrimEnd("/") }
    }
    $cfg | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $config
    Write-Host "Wrote $config (roots: $($found -join ', '))"
}

# where this install came from, for the tray's "Update harbor..."
[ordered]@{ repo = $repo; config = $config; installedAt = (Get-Date).ToString("o") } |
    ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $env:LOCALAPPDATA "harbor\install.json")

$agent = Join-Path $bin "harbor-agent.exe"
$tray = Join-Path $bin "harbor-tray.exe"
if ($Code) {
    & $agent pair $Code --config $config
    if ($LASTEXITCODE -ne 0) { throw "saving the pairing code failed" }
}

# The same shortcut in Startup (runs at login) and the Start menu (start it again after quitting).
$shell = New-Object -ComObject WScript.Shell
function Shortcut([string] $folder) {
    $link = Join-Path $folder "harbor.lnk"
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $tray
    $shortcut.Arguments = "--agent `"$agent`" --config `"$config`""
    $shortcut.WorkingDirectory = $bin
    $shortcut.Description = "harbor tray (runs the harbor agent)"
    $shortcut.Save()
    $link
}
Write-Host "Startup shortcut: $(Shortcut ([Environment]::GetFolderPath('Startup')))"
Write-Host "Start menu: $(Shortcut ([Environment]::GetFolderPath('Programs')))"

Start-Process -FilePath $tray -ArgumentList "--agent", "`"$agent`"", "--config", "`"$config`"" -WorkingDirectory $bin
Write-Host ""
Write-Host "Started. The tray icon turns green once the hub accepts this machine (within a minute with a code)."
Write-Host "Check any time:  & `"$agent`" status --config `"$config`""
