FROM mcr.microsoft.com/windows:1809 AS windows-desktop

FROM mcr.microsoft.com/dotnet/framework/runtime:4.8-windowsservercore-ltsc2019

SHELL ["C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell", "-NonInteractive", "-NoProfile", "-Command", "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue';"]

WORKDIR C:\\unity

# GPU support
COPY system32/* C:\\Windows\\System32\\

# The checked-in DLLs are from Windows 10 build 19041 and import entry points
# unavailable on LTSC 2019. Replace them with the matching Desktop image files.
COPY --from=windows-desktop C:\\Windows\\System32\\ddraw.dll C:\\Windows\\System32\\ddraw.dll
COPY --from=windows-desktop C:\\Windows\\System32\\dsound.dll C:\\Windows\\System32\\dsound.dll
COPY --from=windows-desktop C:\\Windows\\System32\\glu32.dll C:\\Windows\\System32\\glu32.dll
COPY --from=windows-desktop C:\\Windows\\System32\\opengl32.dll C:\\Windows\\System32\\opengl32.dll

# Chocolatey
RUN [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072; \
    iex ((New-Object System.Net.WebClient).DownloadString('https://chocolatey.org/install.ps1'))

# Tools
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0
ENV DOTNET_CLI_UI_LANGUAGE=en
COPY compose-unity.nuspec compose-unity.nuspec
RUN choco pack compose-unity.nuspec; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to pack compose-unity' }; \
    choco install compose-unity --no-progress --yes --ignore-checksums --execution-timeout=7200 --source '.;https://community.chocolatey.org/api/v2/'; \
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
    $process = Start-Process -FilePath $installer -ArgumentList '/S' -PassThru; \
    $process.WaitForExit(); \
    if ($process.ExitCode -ne 0) { throw "Unity Hub installer failed with exit code $($process.ExitCode)" }; \
    $installedVersion = (Get-Item 'C:\Program Files\Unity Hub\Unity Hub.exe').VersionInfo.FileVersion; \
    if ($installedVersion -ne $env:UNITY_HUB_VERSION) { throw "Unexpected Unity Hub version: $installedVersion" }; \
    Remove-Item -Force $installer

# Windows containers cannot start Electron's sandboxed GPU process. Keep Unity's
# documented CLI syntax externally and translate it before launching Hub.
COPY unity-hub-launcher.cs C:\\unity\\unity-hub-launcher.cs
RUN $compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'; \
    $launcher = 'C:\unity\Unity Hub.exe'; \
    $hub = 'C:\Program Files\Unity Hub\Unity Hub.exe'; \
    & $compiler /nologo /optimize+ /target:exe "/out:$launcher" C:\unity\unity-hub-launcher.cs; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to compile the Unity Hub launcher' }; \
    Move-Item -LiteralPath $hub -Destination 'C:\Program Files\Unity Hub\Unity Hub.real.exe'; \
    Move-Item -LiteralPath $launcher -Destination $hub; \
    Remove-Item -Force C:\unity\unity-hub-launcher.cs

# Resume large Editor downloads after temporary CDN stalls.
COPY patch-unity-hub.js C:\\unity\\patch-unity-hub.js
RUN node C:\unity\patch-unity-hub.js; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to enable Unity Hub download retries' }; \
    Remove-Item -Force C:\unity\patch-unity-hub.js

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

RUN compose-unity update --no-interaction --no-dev --optimize-autoloader --classmap-authoritative

# Test
WORKDIR "C:\\Program Files\\Unity Hub"
RUN ["Unity Hub.exe", "--", "--headless", "help"]
