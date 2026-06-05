@echo off
setlocal enabledelayedexpansion

set "LBL_PATH=D:\steam\steamapps\common\Luck be a Landlord"
set "PROJECT_ROOT=%~dp0"

REM ============================================================
REM  Check & auto-install .NET 8 runtime
REM ============================================================
call :EnsureDotNet8
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [FATAL] .NET 8 runtime is required but could not be installed.
    echo Please install it manually: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

REM ============================================================
REM  Deploy DiagnosticMod
REM ============================================================
set "MOD_DEST=%LBL_PATH%\SlotWeave\mods\gdweave.diagnostic"
if not exist "%MOD_DEST%" mkdir "%MOD_DEST%"
copy /Y "%PROJECT_ROOT%SlotWeave-LABL-Template\DiagnosticMod\bin\Debug\net8.0\DiagnosticMod.dll" "%MOD_DEST%\" >nul 2>&1
copy /Y "%PROJECT_ROOT%SlotWeave-LABL-Template\DiagnosticMod\manifest.json" "%MOD_DEST%\" >nul 2>&1

REM ============================================================
REM  Launch
REM ============================================================
set GDWEAVE_DEBUG=1
set GDWEAVE_CONSOLE=1
start "" "%LBL_PATH%\Luck be a Landlord.exe"

echo [OK] Game launched. Log: SlotWeave\SlotWeave.log
endlocal
exit /b 0

REM ============================================================
REM  Subroutine: Ensure .NET 8 runtime is installed
REM ============================================================
:EnsureDotNet8
echo Checking .NET runtime...

REM Try the dotnet command first
where dotnet >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo dotnet CLI not found. Installing .NET 8 runtime...
    goto :DownloadDotNet
)

REM Check if 8.0 runtime is listed
dotnet --list-runtimes 2>nul | findstr /C:"Microsoft.NETCore.App 8." >nul
if %ERRORLEVEL% EQU 0 (
    echo [OK] .NET 8 runtime detected.
    exit /b 0
)

echo .NET 8 runtime not found. Installing...

:DownloadDotNet
set "DOTNET_URL=https://download.visualstudio.microsoft.com/download/pr/9c4cfb7a-f967-4dd8-84d5-f30b9a7b4d9e/3c21d9d8e6a3f0ae2c4f5b6a7c8d9e0f/dotnet-runtime-8.0.10-win-x64.exe"
set "DOTNET_INSTALLER=%TEMP%\dotnet-runtime-8.0.10-win-x64.exe"

echo Downloading .NET 8.0.10 Runtime (x64)...
powershell -Command "Invoke-WebRequest -Uri '%DOTNET_URL%' -OutFile '%DOTNET_INSTALLER%'" 2>nul
if not exist "%DOTNET_INSTALLER%" (
    echo [FAIL] Download failed. Check internet connection.
    exit /b 1
)

echo Installing .NET 8 runtime (silent)...
"%DOTNET_INSTALLER%" /install /quiet /norestart
if %ERRORLEVEL% NEQ 0 (
    echo [FAIL] .NET runtime installation failed.
    del "%DOTNET_INSTALLER%" 2>nul
    exit /b 1
)

del "%DOTNET_INSTALLER%" 2>nul
echo [OK] .NET 8 runtime installed successfully.
exit /b 0
