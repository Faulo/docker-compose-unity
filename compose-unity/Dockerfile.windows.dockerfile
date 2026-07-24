FROM mcr.microsoft.com/windows:1809 AS windows-desktop

FROM mcr.microsoft.com/dotnet/framework/runtime:4.8-windowsservercore-ltsc2019

SHELL ["C:/Windows/System32/WindowsPowerShell/v1.0/powershell", "-NonInteractive", "-NoProfile", "-Command", "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue';"]

WORKDIR C:/unity

# GPU support
COPY --from=windows-desktop C:/Windows/System32/ddraw.dll C:/Windows/System32/ddraw.dll
COPY --from=windows-desktop C:/Windows/System32/dsound.dll C:/Windows/System32/dsound.dll
COPY --from=windows-desktop C:/Windows/System32/glu32.dll C:/Windows/System32/glu32.dll
COPY --from=windows-desktop C:/Windows/System32/opengl32.dll C:/Windows/System32/opengl32.dll

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

RUN choco install 7zip.portable --version=26.2.0 --no-progress --yes; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to install 7-Zip' }

# Unity-generated projects can target .NET Framework 4.7.1 even when a newer
# runtime is installed. Install Microsoft's reference assemblies for MSBuild.
RUN $installer = 'C:\ndp471-devpack-enu.exe'; \
    Invoke-WebRequest -Uri 'https://download.visualstudio.microsoft.com/download/pr/e5eb8d37-5bbd-4fb7-a71d-b749e010ef9f/601437d729667ecd29020a829fbc4881/ndp471-devpack-enu.exe' -OutFile $installer; \
    $actualHash = (Get-FileHash -Algorithm SHA256 $installer).Hash; \
    if ($actualHash -ne 'A70B790DCF7EE4A0CAE65FB82A16FB67FE970EB21B9424C9DA35E1ACAFBC4348') { \
        throw ".NET Framework 4.7.1 Developer Pack checksum mismatch: $actualHash" \
    }; \
    $process = Start-Process -FilePath $installer -ArgumentList '/quiet', '/norestart' -PassThru; \
    if (-not $process.WaitForExit(1200000)) { \
        taskkill.exe /PID $process.Id /T /F | Out-Null; \
        throw '.NET Framework 4.7.1 Developer Pack installer timed out' \
    }; \
    if ($process.ExitCode -notin @(0, 1641, 3010)) { \
        throw ".NET Framework 4.7.1 Developer Pack installer failed with exit code $($process.ExitCode)" \
    }; \
    $referenceAssembly = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.1\mscorlib.dll'; \
    if (-not (Test-Path -LiteralPath $referenceAssembly)) { \
        throw '.NET Framework 4.7.1 reference assemblies were not installed' \
    }; \
    Remove-Item -LiteralPath $installer -Force

# Unity publishes the 3.12.1 installer under its 3.12.0 archive path. Its NSIS
# process can hang on Server Core, so fall back to its verified app archive.
ENV UNITY_HUB_VERSION=3.12.1
RUN $installer = 'C:\UnityHubSetup.exe'; \
    $hubDirectory = 'C:\Program Files\Unity Hub'; \
    $hubExecutable = Join-Path $hubDirectory 'Unity Hub.exe'; \
    Invoke-WebRequest -Uri 'https://public-cdn.cloud.unity3d.com/hub/3.12.0/UnityHubSetup.exe' -OutFile $installer; \
    $actualHash = (Get-FileHash -Algorithm SHA256 $installer).Hash; \
    if ($actualHash -ne '0B8E6941A6A2A7C1DF68B16451E4CC7F8F633C6B5488B21A47860179FD5D8802') { \
        throw "Unity Hub installer checksum mismatch: $actualHash" \
    }; \
    $extractRoot = 'C:\UnityHubSetup'; \
    $sevenZip = 'C:\ProgramData\chocolatey\lib\7zip.portable\tools\7z.exe'; \
    Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue; \
    $extractArgument = '-o' + $extractRoot; \
    & $sevenZip x $installer $extractArgument -y | Out-Null; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to extract the Unity Hub installer' }; \
    $appArchives = @(Get-ChildItem -LiteralPath $extractRoot -Filter 'app-64.7z' -Recurse); \
    if ($appArchives.Count -ne 1) { throw "Expected one Unity Hub app archive, found $($appArchives.Count)" }; \
    $appArchive = $appArchives[0].FullName; \
    $uninstaller = (Get-ChildItem -LiteralPath $extractRoot -Filter 'Uninstall Unity Hub.exe' -Recurse | Select-Object -First 1).FullName; \
    $process = Start-Process -FilePath $installer -ArgumentList '/S' -PassThru; \
    $installerExited = $process.WaitForExit(30000); \
    $needsExtraction = -not $installerExited; \
    if ($installerExited -and $process.ExitCode -ne 0) { $needsExtraction = $true }; \
    if (-not (Test-Path -LiteralPath $hubExecutable)) { $needsExtraction = $true }; \
    if ($needsExtraction) { \
        if (-not $process.HasExited) { \
            taskkill.exe /PID $process.Id /T /F | Out-Null; \
            if (-not $process.HasExited) { $process.Kill() }; \
            $process.WaitForExit() \
        }; \
        Remove-Item -LiteralPath $hubDirectory -Recurse -Force -ErrorAction SilentlyContinue; \
        $hubExtractArgument = '-o' + $hubDirectory; \
        & $sevenZip x $appArchive $hubExtractArgument -y | Out-Null; \
        if ($LASTEXITCODE -ne 0) { throw 'Failed to extract the Unity Hub application' }; \
        Copy-Item -LiteralPath $uninstaller -Destination $hubDirectory \
    }; \
    $installedVersion = (Get-Item $hubExecutable).VersionInfo.FileVersion; \
    if ($installedVersion -ne $env:UNITY_HUB_VERSION) { throw "Unexpected Unity Hub version: $installedVersion" }; \
    $hubRegistryKey = 'HKLM:\SOFTWARE\Unity Technologies\Hub'; \
    New-Item -Path $hubRegistryKey -Force | Out-Null; \
    New-ItemProperty -Path $hubRegistryKey -Name 'InstallLocation' -Value $hubDirectory -PropertyType String -Force | Out-Null; \
    Remove-Item -LiteralPath $extractRoot -Recurse -Force; \
    Remove-Item -Force $installer

# Windows containers cannot start Electron's sandboxed GPU process. Keep Unity's
# documented CLI syntax externally and translate it before launching Hub.
COPY unity-hub-launcher.cs C:/unity/unity-hub-launcher.cs
RUN $compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'; \
    $launcher = 'C:\unity\Unity Hub.exe'; \
    $hub = 'C:\Program Files\Unity Hub\Unity Hub.exe'; \
    & $compiler /nologo /optimize+ /target:exe "/out:$launcher" C:\unity\unity-hub-launcher.cs; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to compile the Unity Hub launcher' }; \
    Move-Item -LiteralPath $hub -Destination 'C:\Program Files\Unity Hub\Unity Hub.real.exe'; \
    Move-Item -LiteralPath $launcher -Destination $hub; \
    Remove-Item -Force C:\unity\unity-hub-launcher.cs

# Resume large Editor downloads after temporary CDN stalls.
COPY patch-unity-hub.js C:/unity/patch-unity-hub.js
RUN node C:\unity\patch-unity-hub.js; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to enable Unity Hub download retries' }; \
    Remove-Item -Force C:\unity\patch-unity-hub.js

COPY php.ini C:/tools/php82/custom.ini
RUN Get-Content C:/tools/php82/custom.ini | Add-Content -Path C:/tools/php82/php.ini

RUN $blenderDirectory = 'C:\Program Files\Blender Foundation\Blender 4.5'; \
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine'); \
    [Environment]::SetEnvironmentVariable('Path', $blenderDirectory + ';' + $machinePath, 'Machine')

# Test
RUN git config --global --add safe.directory *; \
    curl.exe --version; \
    git --version; \
    php --version; \
    composer --version; \
    blender --version; \
    butler --version; \
    npm --version; \
    dotnet --version; \
    docker --version

# Farah
ENV COMPOSE_UNITY="composer -d C:/unity"
ENV COMPOSER_ALLOW_SUPERUSER="1"

ENV UNITY_LOGGING="stdin stdout stderr"
ENV UNITY_ACCELERATOR_ENDPOINT=""
ENV UNITY_NO_GRAPHICS="1"

COPY unity/composer.json C:/unity/
COPY unity/config C:/unity/config/
COPY unity/compose-unity-launcher.cs C:/unity/compose-unity-launcher.cs

RUN $compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'; \
    & $compiler /nologo /optimize+ /target:exe /out:C:\Windows\compose-unity.exe C:\unity\compose-unity-launcher.cs; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to compile the compose-unity launcher' }; \
    Remove-Item -Force C:\unity\compose-unity-launcher.cs

RUN compose-unity update --no-interaction --no-dev --optimize-autoloader --classmap-authoritative

# Test
RUN compose-unity exec unity-build; \
    compose-unity exec unity-help

VOLUME ["C:/Program Files/Unity/Hub/Editor", "C:/Users/ContainerAdministrator/AppData/Roaming/Unity", "C:/Users/ContainerAdministrator/AppData/Roaming/UnityHub", "C:/Users/ContainerAdministrator/AppData/Local/Unity", "C:/ProgramData/Unity"]
