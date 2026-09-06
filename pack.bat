@echo off
chcp 65001 >nul
cd /d "%~dp0"
setlocal EnableExtensions

REM ============================================================
REM  ĐÓNG GÓI BỘ CÀI CHO KHÁCH
REM  Double-click file này. Cần:
REM    - .NET 8 SDK
REM    - Internet lần đầu (tải Chromium ~150MB)
REM    - Inno Setup 6 hoặc 7 (tùy chọn, để ra file Setup.exe)
REM      https://jrsoftware.org/isinfo.php
REM ============================================================

set "PATH=%ProgramFiles%\dotnet;%PATH%"
set "ROOT=%~dp0"
set "PUBLISH=%ROOT%dist\app"
set "DIST=%ROOT%dist"

echo.
echo [1/4] Kiem tra .NET SDK...
dotnet --version
if errorlevel 1 (
    echo.
    echo KHONG TIM THAY dotnet. Cai .NET 8 SDK:
    echo https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

for /f "usebackq delims=" %%v in (`dotnet msbuild "%ROOT%AutoClick.csproj" -nologo -getProperty:Version`) do set "APPVER=%%v"
if not defined APPVER set "APPVER=1.2.0"
echo Phien ban: %APPVER%

echo.
echo [2/4] Publish self-contained win-x64 (khach khong can cai .NET)...
if exist "%PUBLISH%" rmdir /s /q "%PUBLISH%"
dotnet publish "%ROOT%AutoClick.csproj" -c Release -r win-x64 --self-contained true --nologo ^
  -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -p:PlaywrightPlatform=win-x64 ^
  -o "%PUBLISH%"
if errorlevel 1 (
    echo.
    echo PUBLISH THAT BAI — dong AutoClick.exe neu dang mo, roi chay lai.
    pause
    exit /b 1
)

echo.
echo [3/4] Cai Chromium vao thu muc app (ms-playwright)...
set "PLAYWRIGHT_BROWSERS_PATH=%PUBLISH%\ms-playwright"
"%PUBLISH%\AutoClick.exe" --install-chromium
if errorlevel 1 (
    echo.
    echo TAI CHROMIUM THAT BAI — kiem tra mang roi chay lai pack.bat.
    pause
    exit /b 1
)
dir /b /ad "%PUBLISH%\ms-playwright\chromium-*" >nul 2>nul
if errorlevel 1 (
    echo.
    echo KHONG THAY thu muc chromium trong ms-playwright.
    pause
    exit /b 1
)

echo.
echo [4/4] Copy sang installer\files, roi zip + Setup.exe...
set "INNOFILES=%ROOT%installer\files"
if exist "%INNOFILES%" rmdir /s /q "%INNOFILES%"
mkdir "%INNOFILES%"
robocopy "%PUBLISH%" "%INNOFILES%" /E /NFL /NDL /NJH /NJS /nc /ns /np
if errorlevel 8 (
    echo COPY SANG installer\files THAT BAI.
    pause
    exit /b 1
)
> "%ROOT%installer\version.iss" echo #define MyAppVersion "%APPVER%"

if not exist "%DIST%" mkdir "%DIST%"

set "ZIP=%DIST%\AutoClick-portable-%APPVER%.zip"
if exist "%ZIP%" del /f /q "%ZIP%"
tar -a -c -f "%ZIP%" -C "%PUBLISH%" .
if errorlevel 1 (
    echo Zip that bai, thu Compress-Archive...
    powershell -NoProfile -Command "Compress-Archive -Path '%PUBLISH%\*' -DestinationPath '%ZIP%' -Force"
)

set "ISCC="
if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"

if defined ISCC (
    "%ISCC%" /DMyAppVersion=%APPVER% "%ROOT%installer\AutoClick.iss"
    if errorlevel 1 (
        echo.
        echo INNO SETUP BIEN DICH LOI. Van con file zip de gui tam.
    )
) else (
    echo.
    echo Chua cai Inno Setup — chua tao Setup.exe.
    echo Tai: https://jrsoftware.org/isinfo.php
    echo Cai xong chay lai pack.bat de ra AutoClick-Setup-%APPVER%.exe
)

echo.
echo ========================================
echo  XONG
if exist "%DIST%\AutoClick-Setup-%APPVER%.exe" (
    echo  Bo cai:  %DIST%\AutoClick-Setup-%APPVER%.exe
)
echo  Ban zip:  %ZIP%
echo  Gui khach file Setup.exe ^(nen hon zip^).
echo  Setup chua ky so — SmartScreen co the canh bao lan dau.
echo ========================================
echo.
pause
endlocal
