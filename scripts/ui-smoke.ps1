# Builds and drives a real UI smoke test: page navigation, DEMO serial session,
# UI send path, HEX view toggle, theme switch, session recording and local storage.
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "native\FluentSerial\FluentSerial.csproj"
$exe = Join-Path $root "native\FluentSerial\bin\$Configuration\net10.0-windows\FluentSerial.exe"
$dataRoot = Join-Path $root "data\ui-smoke"
$report = Join-Path $dataRoot "ui-smoke.txt"

Get-Process FluentSerial -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 200

Write-Host "Building FluentSerial"
dotnet build $project -c $Configuration | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

if (Test-Path $dataRoot) { Remove-Item $dataRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
$env:FLUENTSERIAL_DATA_DIR = $dataRoot
try {
    $process = Start-Process -FilePath $exe `
        -ArgumentList @("--ui-smoke", "--seed-demo", "--ui-smoke-out", $report) `
        -PassThru -Wait
    if (Test-Path $report) {
        Get-Content $report -Encoding UTF8 | ForEach-Object { Write-Host $_ }
    }
    else {
        throw "UI smoke report was not produced: $report"
    }
    if ($process.ExitCode -ne 0) {
        throw "UI smoke failed with exit code $($process.ExitCode)"
    }
    Write-Host ""
    Write-Host "UI smoke passed."
}
finally {
    Remove-Item Env:\FLUENTSERIAL_DATA_DIR -ErrorAction SilentlyContinue
}
