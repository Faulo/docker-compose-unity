setlocal
cd %~dp0
call load-env
set DOCKER_CONTEXT=dende
for /f %%i in ('docker --context %DOCKER_CONTEXT% info --format "{{.OSType}}"') do SET DOCKER_OS=%%i
pushd compose-unity
call docker run --rm -e UNITY_CREDENTIALS_USR -e UNITY_CREDENTIALS_PSW -e EMAIL_CREDENTIALS_USR -e EMAIL_CREDENTIALS_PSW tmp/%DOCKER_IMAGE%:latest compose-unity exec unity-empty-project test; compose-unity exec unity-build test
popd
endlocal
pause