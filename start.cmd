@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo [FluentSerial] 需要安装 .NET 10 SDK：https://dotnet.microsoft.com/download
  pause
  exit /b 1
)
if exist dist\FluentSerial-native-win-x64\FluentSerial.exe (
  start "" dist\FluentSerial-native-win-x64\FluentSerial.exe
  exit /b 0
)
echo [FluentSerial] 正在构建原生应用...
dotnet build native\FluentSerial\FluentSerial.csproj -c Release
if errorlevel 1 (
  echo [FluentSerial] 构建失败，请查看上方错误信息。
  pause
  exit /b 1
)
echo [FluentSerial] 正在启动...
start "" native\FluentSerial\bin\Release\net10.0-windows\FluentSerial.exe
exit /b 0
