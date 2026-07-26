@echo off
setlocal
cd /d "%~dp0"
call load-env.bat

set "BUILD_EXIT_CODE=1"
set "DOCKER_OS=%~1"
set "DOCKER_CONTEXT_ARGS="

if not defined DOCKER_IMAGE (
    echo Missing DOCKER_IMAGE in .env
    goto build_done
)

if defined DOCKER_OS (
    set "DOCKER_CONTEXT_ARGS=--context %DOCKER_OS%"
) else (
    for /f %%i in ('docker info --format "{{.OSType}}"') do set "DOCKER_OS=%%i"
)

if not defined DOCKER_OS (
    echo Failed to determine the Docker OS
    goto build_done
)

if not exist "%DOCKER_OS%\Dockerfile" (
    echo Missing Dockerfile: %DOCKER_OS%\Dockerfile
    goto build_done
)

pushd "%DOCKER_OS%"
docker %DOCKER_CONTEXT_ARGS% build --pull --tag tmp/%DOCKER_IMAGE%:latest .
set "BUILD_EXIT_CODE=%ERRORLEVEL%"
popd

:build_done
pause
endlocal & exit /b %BUILD_EXIT_CODE%
