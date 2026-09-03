@echo off
chcp 65001 >nul
cd /d "%~dp0"
set "PATH=%ProgramFiles%\dotnet;%PATH%"

REM Build rồi mở app. Nếu chỉ muốn chạy bản đã build, double-click:
REM   bin\Release\net8.0-windows\AutoClick.exe

dotnet build -c Release --nologo
if errorlevel 1 (
    echo BUILD THAT BAI
    pause
    exit /b 1
)

start "" "%~dp0bin\Release\net8.0-windows\AutoClick.exe"
