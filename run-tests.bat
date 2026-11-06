@echo off
dotnet test "%~dp0EasyCraftGen.sln" -c Release
exit /b %errorlevel%
