# Generates deterministic docs screenshots from the real WPF visual tree.
# Each capture runs in its own scratch data directory so no local user data is touched.
param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "docs\native"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "native\FluentSerial\FluentSerial.csproj"
$exe = Join-Path $root "native\FluentSerial\bin\$Configuration\net10.0-windows\FluentSerial.exe"
$outPath = Join-Path $root $OutputDir
$runId = [Guid]::NewGuid().ToString("N")
$dataRoot = Join-Path $root "data\capture\$runId"
$previousDataDirectory = $env:FLUENTSERIAL_DATA_DIR

Write-Host "Building FluentSerial for screenshot capture"
dotnet build $project -c $Configuration | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $exe)) { throw "Executable not found: $exe" }

New-Item -ItemType Directory -Force -Path $outPath | Out-Null

function Invoke-Capture {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$Seed
    )

    $captureDataRoot = Join-Path $dataRoot ([IO.Path]::GetFileNameWithoutExtension($Name))
    New-Item -ItemType Directory -Force -Path $captureDataRoot | Out-Null

    $env:FLUENTSERIAL_DATA_DIR = $captureDataRoot
    $capture = Join-Path $outPath $Name
    # A fresh target also prevents a failed capture from being mistaken for an old screenshot.
    $pendingCapture = Join-Path $outPath ($runId + "-" + $Name)
    $allArgs = @("--capture", ('"{0}"' -f $pendingCapture)) + $Arguments
    if ($Seed) { $allArgs += "--seed-demo" }

    Write-Host "Capturing $Name ..."
    $process = Start-Process -FilePath $exe -ArgumentList $allArgs -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) {
        $process.Kill()
        throw "Capture $Name timed out after 30 seconds. Diagnostics: $captureDataRoot"
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0) {
        throw "Capture $Name exited with code $($process.ExitCode)"
    }
    if (-not (Test-Path -LiteralPath $pendingCapture)) { throw "Capture $Name did not produce an image. Diagnostics: $captureDataRoot" }
    Move-Item -LiteralPath $pendingCapture -Destination $capture -Force
    $file = Get-Item $capture
    Write-Host ("  -> {0} ({1:N0} bytes)" -f $file.Name, $file.Length)
}

try {
    Invoke-Capture -Name "terminal.png" -Seed -Arguments @("--page", "terminal", "--theme", "Light", "--demo", "--delay", "1200")
    Invoke-Capture -Name "terminal-dark.png" -Seed -Arguments @("--page", "terminal", "--theme", "Dark", "--demo", "--delay", "1200")
    Invoke-Capture -Name "terminal-hex.png" -Seed -Arguments @("--page", "terminal", "--theme", "Light", "--demo", "--rx-hex", "--delay", "1200")
    Invoke-Capture -Name "presets.png" -Seed -Arguments @("--page", "presets", "--theme", "Light", "--delay", "500")
    Invoke-Capture -Name "snippets.png" -Seed -Arguments @("--page", "snippets", "--theme", "Light", "--delay", "500")
    Invoke-Capture -Name "logs.png" -Seed -Arguments @("--page", "logs", "--theme", "Light", "--delay", "500")
    Invoke-Capture -Name "logs-audit.png" -Seed -Arguments @("--page", "logs", "--tab", "audit", "--theme", "Light", "--delay", "500")
    Invoke-Capture -Name "settings.png" -Seed -Arguments @("--page", "settings", "--theme", "Light", "--delay", "500")
    Invoke-Capture -Name "settings-dark.png" -Seed -Arguments @("--page", "settings", "--theme", "Dark", "--delay", "500")
    Invoke-Capture -Name "first-run.png" -Arguments @("--page", "terminal", "--theme", "Light", "--delay", "600")
    Invoke-Capture -Name "shortcuts.png" -Seed -Arguments @("--dialog", "shortcuts", "--theme", "Light", "--delay", "500")
    Invoke-Capture -Name "preset-dialog.png" -Seed -Arguments @("--dialog", "preset", "--theme", "Light", "--delay", "500")
    Invoke-Capture -Name "snippet-dialog.png" -Seed -Arguments @("--dialog", "snippet", "--theme", "Light", "--delay", "500")
}
finally {
    $env:FLUENTSERIAL_DATA_DIR = $previousDataDirectory
}

Write-Host ""
Write-Host "Screenshots written to $outPath"
