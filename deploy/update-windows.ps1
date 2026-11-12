# Update harbor on this Windows machine: fast-forward the checkout it was installed from, then
# re-run the installer (rebuild, keep config, restart the tray). The tray's "Update harbor..." runs this.
$ErrorActionPreference = "Stop"
$info = Get-Content -Raw (Join-Path $env:LOCALAPPDATA "harbor\install.json") | ConvertFrom-Json
Write-Host "Updating $($info.repo) ..."
git -C $info.repo pull --ff-only
if ($LASTEXITCODE -ne 0) { throw "git pull --ff-only failed (local changes or diverged?); resolve in $($info.repo) and re-run" }
& (Join-Path $info.repo "deploy\install-windows.ps1")
Write-Host "Done. This window can be closed."
