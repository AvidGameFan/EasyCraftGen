@echo off
setlocal
set "VERSION=%~1"
if not defined VERSION set /p VERSION=<"%~dp0version.txt"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-package.ps1" -Runtime win-x64 -Version "%VERSION%"
exit /b %errorlevel%
