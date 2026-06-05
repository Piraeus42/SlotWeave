@echo off
setlocal

REM ================================================
REM  SlotWeave Build & Deploy (development)
REM  Modify GAME_ROOT to your Luck be a Landlord path
REM ================================================

set GAME_ROOT=D:\steam\steamapps\common\Luck be a Landlord
set GDWEAVE_DIR=%GAME_ROOT%\SlotWeave

echo Building SlotWeave...
dotnet build SlotWeave.sln -c Release -p:SlotWeavePath="%GDWEAVE_DIR%"
if %ERRORLEVEL% NEQ 0 (
    echo BUILD FAILED
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Deployed to %GDWEAVE_DIR%\core\
echo Done.
