@echo off
setlocal
if "%~1"=="" (echo Usage: build.bat ^<version^> [-SkipDeploy] [-Configuration Debug] & echo Example: build.bat 1.1.0.0 & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
