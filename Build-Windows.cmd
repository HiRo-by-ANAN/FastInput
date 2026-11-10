@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

echo FastInput Windows x64 build
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-windows.ps1"
set "BUILD_EXIT=%ERRORLEVEL%"
echo.
if not "%BUILD_EXIT%"=="0" (
    echo Build failed. Read the message above and README.md, then run this file again.
) else (
    echo Build complete. Open dist\FastInput-windows\FastInput.exe to test.
)
echo.
pause
exit /b %BUILD_EXIT%
