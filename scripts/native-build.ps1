# Builds the native FluentSerial WPF application in Release configuration.
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "native\FluentSerial\FluentSerial.csproj"

Write-Host "Building FluentSerial ($Configuration)"
dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

$exe = Join-Path $root "native\FluentSerial\bin\$Configuration\net10.0-windows\FluentSerial.exe"
Write-Host ""
Write-Host "Executable: $exe"
