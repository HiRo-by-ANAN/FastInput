@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
if exist "%SCRIPT_DIR%..\dist\FastInput-windows\FastInput.exe" (
    set "APP_BINARY=%SCRIPT_DIR%..\dist\FastInput-windows\FastInput.exe"
) else if exist "%SCRIPT_DIR%FastInput.exe" (
    set "APP_BINARY=%SCRIPT_DIR%FastInput.exe"
) else (
    echo FastInput.exe not found. Build it first with Build-Windows.cmd.
    pause
    exit /b 1
)

"%APP_BINARY%" %*
if errorlevel 1 pause
