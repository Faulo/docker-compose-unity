@echo off
call "%~dp0docker-test.bat" windows gpu
exit /b %ERRORLEVEL%
