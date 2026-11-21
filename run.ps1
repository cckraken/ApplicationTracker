# Starts ApplicationTracker for real use with the REAL database (data/tracker.db)
# and opens it in the default browser. Press Ctrl+C in this window to stop it.
#
# Run it with:  powershell -ExecutionPolicy Bypass -File .\run.ps1
# (Windows blocks unsigned scripts by default; this allows just this run.)
#
# Settings are passed as arguments rather than environment variables, so this
# window isn't left in Production mode afterwards (which would point `dotnet ef`
# at the real database).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$url = 'http://localhost:5180'

Write-Host "Starting ApplicationTracker at $url (real database: data\tracker.db) ..."

# Open the browser once the app answers.
Start-Job -ScriptBlock {
    param($u)
    for ($i = 0; $i -lt 60; $i++) {
        try { Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 2 | Out-Null; Start-Process $u; return }
        catch { Start-Sleep -Seconds 1 }
    }
} -ArgumentList $url | Out-Null

dotnet run --project (Join-Path $root 'src/ApplicationTracker.App') --no-launch-profile -- `
    --environment Production `
    --urls $url `
    --Tracker:DatabaseFile tracker.db
