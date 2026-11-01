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

dotnet publish $project -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $outputPath
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $outputPath "FluentSerial.exe"
if (-not (Test-Path $exe)) {
    throw "Publish completed but FluentSerial.exe was not produced."
}

$zip = $outputPath.TrimEnd('\', '/') + ".zip"
$pendingZip = $zip + "." + [Guid]::NewGuid().ToString("N") + ".zip"
Compress-Archive -Path (Join-Path $outputPath "*") -DestinationPath $pendingZip -CompressionLevel Optimal
Move-Item -LiteralPath $pendingZip -Destination $zip -Force

Write-Host ""
Write-Host "Native executable: $exe"
Write-Host "Portable archive:  $zip"
