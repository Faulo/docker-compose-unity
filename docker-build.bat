setlocal
cd %~dp0
call load-env
for /f %%i in ('docker info --format "{{.OSType}}"') do SET DOCKER_OS=%%i
call docker build -t tmp/compose-unity:latest %DOCKER_OS%
endlocal
pause