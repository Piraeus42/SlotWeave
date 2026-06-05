@echo off
setlocal enabledelayedexpansion

set "LBL_PATH=D:\steam\steamapps\common\Luck be a Landlord"

echo ============================================================
echo  SlotWeave Launcher for Luck be a Landlord
echo ============================================================
echo.

REM ============================================================
REM  Check & auto-install .NET 8 runtime
REM ============================================================
call :EnsureDotNet8
if %ERRORLEVEL% NEQ 0 (
    echo [FATAL] .NET 8 runtime required. Install manually:
    echo   https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

REM ============================================================
REM  Launch game
REM ============================================================
echo.
echo Launching Luck be a Landlord...
start "" "%LBL_PATH%\Luck be a Landlord.exe"
echo [OK] Game launched.
endlocal
exit /b 0

REM ============================================================
REM  Subroutine: Ensure .NET 8 runtime
REM ============================================================
:EnsureDotNet8
where dotnet >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    dotnet --list-runtimes 2>nul | findstr /C:"Microsoft.NETCore.App 8." >nul
    if %ERRORLEVEL% EQU 0 (
        echo [OK] .NET 8 runtime found.
        exit /b 0
    )
)

echo .NET 8 runtime not found. Downloading installer script...
set "PS_SCRIPT=%TEMP%\dotnet-install.ps1"

REM Download Microsoft's official install script
powershell -Command "Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile '%PS_SCRIPT%'" 2>nul
if not exist "%PS_SCRIPT%" (
    echo [FAIL] Cannot download dotnet-install.ps1. Check internet.
    exit /b 1
)

echo Installing .NET 8 runtime...
powershell -ExecutionPolicy Bypass -File "%PS_SCRIPT%" -Channel 8.0 -Runtime dotnet
if %ERRORLEVEL% NEQ 0 (
    echo [FAIL] Installation failed.
    exit /b 1
)

echo [OK] .NET 8 runtime installed.
exit /b 0
