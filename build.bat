@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
title Oyun Asistani - Derleme

set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

"%DOTNET%" --list-sdks 2>nul | findstr /r /c:"^[89]\." /c:"^1[0-9]\." >nul
if errorlevel 1 (
    echo [1/3] .NET 8 SDK bulunamadi, winget ile kuruluyor...
    winget install -e --id Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements
    set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
) else (
    echo [1/3] .NET SDK hazir.
)

echo [2/3] Derleniyor...
"%DOTNET%" publish InGameCompanion.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
if errorlevel 1 (
    echo.
    echo DERLEME BASARISIZ. Yukaridaki hatayi Claude'a gonder.
    pause
    exit /b 1
)

echo [3/3] Tamam: dist\InGameCompanion.exe
echo.
echo Ilk calistirmada dist\settings.json olusur ve Not Defteri'nde acilir.
echo "ApiKey" alanina Gemini API anahtarini yaz, kaydet, tepsi menusunden "Ayarlari yeniden yukle".
echo.
start "" "dist\InGameCompanion.exe"
endlocal
