FROM mcr.microsoft.com/dotnet/framework/runtime:4.8-windowsservercore-ltsc2019

SHELL ["C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell", "-NonInteractive", "-NoProfile", "-Command", "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue';"]

WORKDIR C:\\unity

# GPU support
COPY system32/* C:\\Windows\\System32\\

# Chocolatey
RUN [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072; \
    iex ((New-Object System.Net.WebClient).DownloadString('https://chocolatey.org/install.ps1'))

# Tools
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0
ENV DOTNET_CLI_UI_LANGUAGE=en
COPY compose-unity.nuspec compose-unity.nuspec
RUN choco pack compose-unity.nuspec; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to pack compose-unity' }; \
    choco install compose-unity --no-progress --yes --ignore-checksums --source '.;https://community.chocolatey.org/api/v2/'; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to install compose-unity dependencies' }; \
    Remove-Item -Force *.nuspec; \
    Remove-Item -Force *.nupkg

# Unity publishes the 3.12.1 installer under its 3.12.0 archive path.
ENV UNITY_HUB_VERSION=3.12.1
RUN $installer = 'C:\UnityHubSetup.exe'; \
    Invoke-WebRequest -Uri 'https://public-cdn.cloud.unity3d.com/hub/3.12.0/UnityHubSetup.exe' -OutFile $installer; \
    $actualHash = (Get-FileHash -Algorithm SHA256 $installer).Hash; \
    if ($actualHash -ne '0B8E6941A6A2A7C1DF68B16451E4CC7F8F633C6B5488B21A47860179FD5D8802') { \
        throw "Unity Hub installer checksum mismatch: $actualHash" \
    }; \
    $process = Start-Process -FilePath $installer -ArgumentList '/S' -PassThru -Wait; \
    if ($process.ExitCode -ne 0) { throw "Unity Hub installer failed with exit code $($process.ExitCode)" }; \
    $installedVersion = (Get-Item 'C:\Program Files\Unity Hub\Unity Hub.exe').VersionInfo.FileVersion; \
    if ($installedVersion -ne $env:UNITY_HUB_VERSION) { throw "Unexpected Unity Hub version: $installedVersion" }; \
    Remove-Item -Force $installer

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

ENV UNITY_LOGGING="stdin stdout stderr"
ENV UNITY_ACCELERATOR_ENDPOINT=""
ENV UNITY_NO_GRAPHICS="1"

COPY unity/composer.json C:\\unity\\
COPY unity/config C:\\unity\\config\\
COPY unity/compose-unity.bat C:\\Windows\\

RUN compose-unity update --no-interaction --no-dev --optimize-autoloader --classmap-authoritative; \
    compose-unity exec unity-build
