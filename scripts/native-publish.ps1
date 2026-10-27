# Publishes FluentSerial as a self-contained single-file Windows executable and a portable zip.
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "dist\FluentSerial-native-win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "native\FluentSerial\FluentSerial.csproj"
$outputPath = Join-Path $root $Output

Write-Host "Publishing FluentSerial Native to $outputPath"
Get-Process FluentSerial -ErrorAction SilentlyContinue | Stop-Process -Force

dotnet publish $project -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -o $outputPath
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Get-ChildItem $outputPath -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

$exe = Join-Path $outputPath "FluentSerial.exe"
if (-not (Test-Path $exe)) {
    throw "Publish completed but FluentSerial.exe was not produced."
}

$zip = Join-Path $root "dist\FluentSerial-native-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $outputPath "*") -DestinationPath $zip -CompressionLevel Optimal

Write-Host ""
Write-Host "Native executable: $exe"
Write-Host "Portable archive:  $zip"
