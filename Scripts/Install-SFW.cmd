@echo off
setlocal
echo Installing SFW for the current Windows user.
echo Windows may ask permission to trust the publisher certificate.
echo Keep this window open until installation finishes.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Add-AppDevPackage.ps1" -SkipLoggingTelemetry
set "installResult=%ERRORLEVEL%"
if not "%installResult%"=="0" echo Installation failed with code %installResult%. Please keep the error text above.
pause
exit /b %installResult%
