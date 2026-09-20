@echo off
rem One-click wrapper for NordicandiaPatcher.ps1.
rem Edit DEFAULT_BACKEND below (or pass -BackendHost to the script) before sharing.
setlocal
set "DEFAULT_BACKEND=3.140.50.136"
set "SCRIPT_DIR=%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%NordicandiaPatcher.ps1" ^
    -BackendHost "%DEFAULT_BACKEND%" %*

if errorlevel 1 (
    echo.
    echo The patcher reported an error. See the messages above.
    pause
    exit /b 1
)
pause