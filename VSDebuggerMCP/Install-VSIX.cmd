@echo off
setlocal

set SCRIPT_DIR=%~dp0
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Install-VSIX.ps1" %*

if errorlevel 1 (
  echo.
  echo VSIX install failed.
  exit /b 1
)

echo.
echo VSIX install completed.
exit /b 0
