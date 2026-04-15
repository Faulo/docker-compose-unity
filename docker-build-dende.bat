setlocal
cd %~dp0
call load-env
set DOCKER_CONTEXT=dende
for /f %%i in ('docker --context %DOCKER_CONTEXT% info --format "{{.OSType}}"') do SET DOCKER_OS=%%i
pushd compose-unity
call docker --context %DOCKER_CONTEXT% build -t tmp/%DOCKER_IMAGE%:latest . -f Dockerfile.%DOCKER_OS%.dockerfile
popd
endlocal
pause