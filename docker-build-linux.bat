@echo off
call "%~dp0docker-build.bat" linux
exit /b %ERRORLEVEL%
