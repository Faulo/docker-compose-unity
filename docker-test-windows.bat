@echo off
call "%~dp0docker-test.bat" windows
exit /b %ERRORLEVEL%
