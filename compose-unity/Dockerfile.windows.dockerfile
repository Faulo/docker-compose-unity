FROM mcr.microsoft.com/dotnet/framework/runtime:4.8-windowsservercore-ltsc2019

SHELL ["C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell", "-NonInteractive", "-NoProfile", "-Command", "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue';"]

WORKDIR C:\\unity

# GPU support
COPY system32/* C:\\Windows\\System32\\

# Chocolatey
RUN [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072; \
    iex ((New-Object System.Net.WebClient).DownloadString('https://chocolatey.org/install.ps1'))

# Tools
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="1"
ENV DOTNET_CLI_UI_LANGUAGE="en"
COPY compose-unity.nuspec compose-unity.nuspec
RUN choco pack compose-unity.nuspec; \
    choco install compose-unity --no-progress --yes --ignore-checksums --source '.;https://community.chocolatey.org/api/v2/'; \
    Remove-Item -Force *.nuspec; \
    Remove-Item -Force *.nupkg

COPY php.ini C:\\tools\\php82\\custom.ini
RUN Get-Content C:\\tools\\php82\\custom.ini | Add-Content -Path C:\\tools\\php82\\php.ini

# Test
RUN git config --global --add safe.directory *; \
    curl.exe --version; \
    git --version; \    
    php --version; \
    composer --version; \
    butler --version; \
    npm --version; \
    dotnet --version; \
    docker --version

# Farah
ENV COMPOSE_UNITY="composer -d C:\\unity"
ENV COMPOSER_ALLOW_SUPERUSER="1"
COPY unity/composer.json C:\\unity\\
COPY unity/config C:\\unity\\config\\
COPY unity/compose-unity.bat C:\\Windows\\
RUN compose-unity update --no-interaction --no-dev --optimize-autoloader --classmap-authoritative; \
    compose-unity exec unity-build
