@echo off
setlocal
cd /d "%~dp0"

if exist "dist\FluentSerial-native-win-x64\FluentSerial.exe" goto :runpublished

where dotnet >nul 2>nul
if errorlevel 1 goto :nodotnet

echo [FluentSerial] Building native app...
dotnet build "native\FluentSerial\FluentSerial.csproj" -c Release
if errorlevel 1 goto :buildfailed
echo [FluentSerial] Starting...
start "" "native\FluentSerial\bin\Release\net10.0-windows\FluentSerial.exe"
exit /b 0

:runpublished
start "" "dist\FluentSerial-native-win-x64\FluentSerial.exe"
exit /b 0

:nodotnet
echo [FluentSerial] .NET 10 SDK is required: https://dotnet.microsoft.com/download
pause
exit /b 1

:buildfailed
echo [FluentSerial] Build failed. See the output above.
pause
exit /b 1
