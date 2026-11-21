# PostToolUse hook: run `dotnet format` on a .cs file Claude just edited.
# Exits 0 silently when the file isn't C#, or no .sln/.csproj exists yet.
$ErrorActionPreference = 'Stop'
try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $file = $payload.tool_input.file_path
    if (-not $file -or -not $file.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase)) { exit 0 }
    if (-not (Test-Path -LiteralPath $file)) { exit 0 }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { exit 0 }

    $projectDir = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { (Get-Location).Path }

    # Prefer a solution at the project root; otherwise the nearest .csproj above the file.
    $workspace = Get-ChildItem -LiteralPath $projectDir -Filter '*.sln*' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.sln', '.slnx' } | Select-Object -First 1
    if (-not $workspace) {
        $dir = Split-Path -Parent (Resolve-Path -LiteralPath $file).Path
        while ($dir -and -not $workspace) {
            $workspace = Get-ChildItem -LiteralPath $dir -Filter '*.csproj' -File -ErrorAction SilentlyContinue | Select-Object -First 1
            $parent = Split-Path -Parent $dir
            if ($parent -eq $dir) { break }
            $dir = $parent
        }
    }
    if (-not $workspace) { exit 0 }

    # dotnet format matches --include against paths relative to the workspace folder.
    $wsDir = $workspace.DirectoryName
    $fullFile = (Resolve-Path -LiteralPath $file).Path
    Push-Location $wsDir
    try {
        $relative = Resolve-Path -LiteralPath $fullFile -Relative
        & dotnet format $workspace.FullName --include $relative --verbosity quiet 2>&1 | Out-Null
    }
    finally { Pop-Location }
    exit 0
}
catch {
    # Never block Claude's edit because formatting failed.
    exit 0
}
