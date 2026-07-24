@echo off
call "%~dp0docker-test.bat" linux
exit /b %ERRORLEVEL%
