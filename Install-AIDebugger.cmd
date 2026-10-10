@echo off
setlocal
py -3 "%~dp0tools\install_aidebugger.py" %*
exit /b %errorlevel%
