@echo off
chcp 65001 >nul
cd /d "%~dp0"
setlocal EnableExtensions

REM ============================================================
REM  CHUẨN BỊ FILE CHO INNO SETUP COMPILER
REM  Double-click file này, đợi xong, rồi mở:
REM    installer\AutoClick.iss
REM  trong Inno Setup Compiler → Build → Compile
REM ============================================================

set "PATH=%ProgramFiles%\dotnet;%PATH%"
set "ROOT=%~dp0"
set "PUBLISH=%ROOT%dist\app"
set "INNOFILES=%ROOT%installer\files"
set "PAUSE_AT_END=1"
if /i "%~1"=="nopause" set "PAUSE_AT_END=0"

echo.
echo [1/4] Kiem tra .NET SDK...
dotnet --version
if errorlevel 1 (
    echo.
    echo KHONG TIM THAY dotnet. Cai .NET 8 SDK:
    echo https://dotnet.microsoft.com/download/dotnet/8.0
    if "%PAUSE_AT_END%"=="1" pause
    exit /b 1
)

for /f "usebackq delims=" %%v in (`dotnet msbuild "%ROOT%AutoClick.csproj" -nologo -getProperty:Version`) do set "APPVER=%%v"
if not defined APPVER set "APPVER=1.2.0"
echo Phien ban: %APPVER%

echo.
echo [2/4] Publish self-contained win-x64...
if exist "%PUBLISH%" rmdir /s /q "%PUBLISH%"
dotnet publish "%ROOT%AutoClick.csproj" -c Release -r win-x64 --self-contained true --nologo ^
  -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -p:PlaywrightPlatform=win-x64 ^
  -o "%PUBLISH%"
if errorlevel 1 (
    echo.
    echo PUBLISH THAT BAI — dong AutoClick.exe neu dang mo, roi chay lai.
    if "%PAUSE_AT_END%"=="1" pause
    exit /b 1
)

echo.
echo [3/4] Cai Chromium vao thu muc app (ms-playwright)...
set "PLAYWRIGHT_BROWSERS_PATH=%PUBLISH%\ms-playwright"
"%PUBLISH%\AutoClick.exe" --install-chromium
if errorlevel 1 (
    echo.
    echo TAI CHROMIUM THAT BAI — kiem tra mang roi chay lai prepare-inno.bat.
    if "%PAUSE_AT_END%"=="1" pause
    exit /b 1
)
dir /b /ad "%PUBLISH%\ms-playwright\chromium-*" >nul 2>nul
if errorlevel 1 (
    echo.
    echo KHONG THAY thu muc chromium trong ms-playwright.
    if "%PAUSE_AT_END%"=="1" pause
    exit /b 1
)

echo.
echo [4/4] Copy toan bo file sang installer\files cho Inno Setup...
if exist "%INNOFILES%" rmdir /s /q "%INNOFILES%"
mkdir "%INNOFILES%"
robocopy "%PUBLISH%" "%INNOFILES%" /E /NFL /NDL /NJH /NJS /nc /ns /np
if errorlevel 8 (
    echo COPY THAT BAI.
    if "%PAUSE_AT_END%"=="1" pause
    exit /b 1
)

> "%ROOT%installer\version.iss" echo #define MyAppVersion "%APPVER%"

echo.
echo ========================================
echo  XONG — san sang bien dich Inno Setup
echo  1. Mo Inno Setup Compiler
echo  2. File ^> Open: %ROOT%installer\AutoClick.iss
echo  3. Build ^> Compile
echo  Setup ra: %ROOT%dist\AutoClick-Setup-%APPVER%.exe
echo ========================================
echo.
if "%PAUSE_AT_END%"=="1" pause
endlocal
exit /b 0
