# FluentSerial native smoke test runner.
# Builds the WPF app and then runs the console smoke project, which exercises the
# serial core (codec, checksums, recorder, data store, DEMO transport, session service).
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $root "native\FluentSerial\FluentSerial.csproj"
$smokeProject = Join-Path $root "native\FluentSerial.Smoke\FluentSerial.Smoke.csproj"

Write-Host "==> dotnet build $appProject -c $Configuration" -ForegroundColor Cyan
dotnet build $appProject -c $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Host "FluentSerial build failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "==> dotnet run --project $smokeProject -c $Configuration" -ForegroundColor Cyan
dotnet run --project $smokeProject -c $Configuration
$exit = $LASTEXITCODE
if ($exit -ne 0) {
    Write-Host "Smoke test failed (exit $exit)." -ForegroundColor Red
}
else {
    Write-Host "Smoke test passed." -ForegroundColor Green
}
exit $exit
