@echo off
call "%~dp0docker-build.bat" windows
exit /b %ERRORLEVEL%
