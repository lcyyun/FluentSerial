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
$dataRoot = Join-Path $root "data\demo"

Get-Process FluentSerial -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

Write-Host "Building FluentSerial for screenshot capture"
dotnet build $project -c $Configuration | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $exe)) { throw "Executable not found: $exe" }

New-Item -ItemType Directory -Force -Path $outPath | Out-Null
Get-Process FluentSerial -ErrorAction SilentlyContinue | Stop-Process -Force

function Invoke-Capture {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$FreshData,
        [switch]$Seed
    )

    if ($FreshData) {
        if (Test-Path $dataRoot) { Remove-Item $dataRoot -Recurse -Force }
    }
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null

    $env:FLUENTSERIAL_DATA_DIR = $dataRoot
    $capture = Join-Path $outPath $Name
    $allArgs = @("--capture", $capture) + $Arguments
    if ($Seed) { $allArgs += "--seed-demo" }

    Write-Host "Capturing $Name ..."
    $process = Start-Process -FilePath $exe -ArgumentList $allArgs -PassThru -Wait
    if ($process.ExitCode -ne 0) {
        throw "Capture $Name exited with code $($process.ExitCode)"
    }
    Start-Sleep -Milliseconds 300
    if (-not (Test-Path $capture)) { throw "Capture $Name did not produce $capture" }
    $file = Get-Item $capture
    Write-Host ("  -> {0} ({1:N0} bytes)" -f $file.Name, $file.Length)
}

try {
    Invoke-Capture -Name "terminal.png" -Seed -FreshData -Arguments @("--page", "terminal", "--theme", "Light", "--demo", "--delay", "1200")
    Invoke-Capture -Name "terminal-dark.png" -Seed -FreshData -Arguments @("--page", "terminal", "--theme", "Dark", "--demo", "--delay", "1200")
    Invoke-Capture -Name "terminal-hex.png" -Seed -FreshData -Arguments @("--page", "terminal", "--theme", "Light", "--demo", "--rx-hex", "--delay", "1200")
    Invoke-Capture -Name "presets.png" -Seed -FreshData -Arguments @("--page", "presets", "--theme", "Light", "--delay", "500")
    Invoke-Capture -Name "snippets.png" -Seed -FreshData -Arguments @("--page", "snippets", "--theme", "Light", "--delay", "500")
    Invoke-Capture -Name "logs.png" -Seed -FreshData -Arguments @("--page", "logs", "--theme", "Light", "--delay", "500")
    Invoke-Capture -Name "logs-audit.png" -Seed -FreshData -Arguments @("--page", "logs", "--tab", "audit", "--theme", "Light", "--delay", "500")
    Invoke-Capture -Name "settings.png" -Seed -FreshData -Arguments @("--page", "settings", "--theme", "Light", "--delay", "500")
    Invoke-Capture -Name "settings-dark.png" -Seed -FreshData -Arguments @("--page", "settings", "--theme", "Dark", "--delay", "500")
    Invoke-Capture -Name "first-run.png" -FreshData -Arguments @("--page", "terminal", "--theme", "Light", "--delay", "600")
    Invoke-Capture -Name "shortcuts.png" -Seed -FreshData -Arguments @("--dialog", "shortcuts", "--theme", "Light", "--delay", "500")
    Invoke-Capture -Name "preset-dialog.png" -Seed -FreshData -Arguments @("--dialog", "preset", "--theme", "Light", "--delay", "500")
    Invoke-Capture -Name "snippet-dialog.png" -Seed -FreshData -Arguments @("--dialog", "snippet", "--theme", "Light", "--delay", "500")
}
finally {
    Remove-Item Env:\FLUENTSERIAL_DATA_DIR -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Screenshots written to $outPath"
