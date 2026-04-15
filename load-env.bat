FOR /F "tokens=*" %%i IN ('type .env') DO SET %%i
FOR /F "tokens=*" %%i IN ('docker info --format {{.OSType}}') DO SET DOCKER_OS_TYPE=%%i
