@echo off
chcp 65001 >nul
cd /d "%~dp0"

REM ============================================================
REM  BUILD LẠI APP SAU KHI SỬA CODE
REM  Cách 1: double-click file này
REM  Cách 2: mở Terminal tại C:\apps\autoclick rồi gõ:
REM           dotnet build -c Release
REM ============================================================

set "PATH=%ProgramFiles%\dotnet;%PATH%"

echo.
echo [1/2] Kiem tra .NET SDK...
dotnet --version
if errorlevel 1 (
    echo.
    echo KHONG TIM THAY dotnet. Cai .NET 8 SDK:
    echo https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

echo.
echo [2/2] Build Release...
dotnet build -c Release --nologo
if errorlevel 1 (
    echo.
    echo BUILD THAT BAI — xem loi o tren.
    pause
    exit /b 1
)

echo.
echo ========================================
echo  THANH CONG
echo  File chay:
echo  %~dp0bin\Release\net8.0-windows\AutoClick.exe
echo ========================================
echo.
pause
