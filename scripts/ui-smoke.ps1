# Builds and drives a real UI smoke test: page navigation, DEMO serial session,
# UI send path, HEX view toggle, theme switch, session recording and local storage.
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "native\FluentSerial\FluentSerial.csproj"
$exe = Join-Path $root "native\FluentSerial\bin\$Configuration\net10.0-windows\FluentSerial.exe"
$runId = [Guid]::NewGuid().ToString("N")
$dataRoot = Join-Path $root "data\ui-smoke\$runId"
$report = Join-Path $dataRoot "ui-smoke.txt"

Write-Host "Building FluentSerial"
dotnet build $project -c $Configuration | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
$previousDataDirectory = $env:FLUENTSERIAL_DATA_DIR
$env:FLUENTSERIAL_DATA_DIR = $dataRoot
try {
    $process = Start-Process -FilePath $exe `
        -ArgumentList @("--ui-smoke", "--seed-demo", "--ui-smoke-out", ('"{0}"' -f $report)) `
        -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) {
        # Only terminate the isolated process this script owns, never a user's session.
        $process.Kill()
        throw "UI smoke timed out after 60 seconds. Diagnostics: $dataRoot"
    }
    $process.Refresh()
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
    Write-Host "Report: $report"
}
finally {
    $env:FLUENTSERIAL_DATA_DIR = $previousDataDirectory
}
