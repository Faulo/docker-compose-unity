FROM mcr.microsoft.com/windows:1809 AS windows-desktop

FROM mcr.microsoft.com/dotnet/framework/runtime:4.8-windowsservercore-ltsc2019

SHELL ["C:/Windows/System32/WindowsPowerShell/v1.0/powershell", "-NonInteractive", "-NoProfile", "-Command", "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue';"]

WORKDIR C:/unity

# GPU support
COPY --from=windows-desktop ["C:/Windows/System32/ddraw.dll", "C:/Windows/System32/dsound.dll", "C:/Windows/System32/glu32.dll", "C:/Windows/System32/opengl32.dll", "C:/Windows/System32/avicap32.dll", "C:/Windows/System32/msvfw32.dll", "C:/Windows/System32/"]

# Chocolatey
# Parentheses disambiguate PowerShell's type expression from Docker's exec form.
RUN ([System.Net.ServicePointManager]::SecurityProtocol) = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072; \
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
# runtime is installed. The Developer Pack installer hangs under Hyper-V
# isolation, so install Microsoft's reference-assemblies package for MSBuild.
RUN $package = 'C:\net471-reference-assemblies.nupkg'; \
    $extractRoot = 'C:\net471-reference-assemblies'; \
    $packageUri = 'https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net471/1.0.3/microsoft.netframework.referenceassemblies.net471.1.0.3.nupkg'; \
    Invoke-WebRequest -Uri $packageUri -OutFile $package; \
    $actualHash = (Get-FileHash -Algorithm SHA256 $package).Hash; \
    if ($actualHash -ne '2D1662724B3794992640C59BFCEB2F010E62A3BDB581A626D1B6D7BD5149D575') { \
        throw ".NET Framework 4.7.1 reference assemblies checksum mismatch: $actualHash" \
    }; \
    $sevenZip = 'C:\ProgramData\chocolatey\lib\7zip.portable\tools\7z.exe'; \
    $extractArgument = '-o' + $extractRoot; \
    & $sevenZip x $package $extractArgument -y | Out-Null; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to extract .NET Framework 4.7.1 reference assemblies' }; \
    $packageAssemblies = Join-Path $extractRoot 'build\.NETFramework\v4.7.1'; \
    $referenceDirectory = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.1'; \
    New-Item -ItemType Directory -Path $referenceDirectory -Force | Out-Null; \
    Copy-Item -Path (Join-Path $packageAssemblies '*') -Destination $referenceDirectory -Recurse -Force; \
    $referenceAssembly = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.1\mscorlib.dll'; \
    if (-not (Test-Path -LiteralPath $referenceAssembly)) { \
        throw '.NET Framework 4.7.1 reference assemblies were not installed' \
    }; \
    Remove-Item -LiteralPath $package, $extractRoot -Recurse -Force

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

# Resume large Editor downloads after temporary CDN stalls and launch the
# installer helper directly because Windows containers cannot display UAC.
COPY patch-unity-hub.js C:/unity/patch-unity-hub.js
RUN node C:\unity\patch-unity-hub.js; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to patch Unity Hub for Windows containers' }; \
    Remove-Item -Force C:\unity\patch-unity-hub.js

COPY php-extensions.ini C:/tools/php82/compose-unity-extensions.ini
RUN Get-Content C:/tools/php82/compose-unity-extensions.ini | Add-Content -Path C:/tools/php82/php.ini

RUN $blenderDirectory = 'C:\Program Files\Blender Foundation\Blender 4.5'; \
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine'); \
    [Environment]::SetEnvironmentVariable('Path', $blenderDirectory + ';' + $machinePath, 'Machine')

# Test
RUN git config --global --add safe.directory *; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to configure Git safe directories' }; \
    curl.exe --version; \
    if ($LASTEXITCODE -ne 0) { throw 'curl smoke check failed' }; \
    git --version; \
    if ($LASTEXITCODE -ne 0) { throw 'Git smoke check failed' }; \
    php --version; \
    if ($LASTEXITCODE -ne 0) { throw 'PHP smoke check failed' }; \
    composer --version; \
    if ($LASTEXITCODE -ne 0) { throw 'Composer smoke check failed' }; \
    blender --version; \
    if ($LASTEXITCODE -ne 0) { throw 'Blender smoke check failed' }; \
    butler --version; \
    if ($LASTEXITCODE -ne 0) { throw 'Butler smoke check failed' }; \
    npm --version; \
    if ($LASTEXITCODE -ne 0) { throw 'npm smoke check failed' }; \
    dotnet --version; \
    if ($LASTEXITCODE -ne 0) { throw '.NET smoke check failed' }; \
    docker --version; \
    if ($LASTEXITCODE -ne 0) { throw 'Docker smoke check failed' }

# Farah
ENV COMPOSE_UNITY="composer -d C:/unity"
ENV COMPOSER_ALLOW_SUPERUSER="1"

ENV UNITY_LOGGING="stdin stdout stderr"
ENV UNITY_ACCELERATOR_ENDPOINT=""
ENV UNITY_NO_GRAPHICS="1"

# Windows containers cannot start Electron's sandboxed GPU process. Keep Unity's
# documented CLI syntax externally and translate it before launching Hub.
COPY ["Unity Hub.cs", "compose-unity.cs", "C:/unity/"]
RUN $project = 'C:\unity\launcher.csproj'; \
    $hubSource = 'C:\unity\Unity Hub.cs'; \
    $composeSource = 'C:\unity\compose-unity.cs'; \
    $hubPublish = 'C:\unity\publish-hub'; \
    $composePublish = 'C:\unity\publish-compose'; \
    Set-Content -LiteralPath $project -Encoding UTF8 -Value @( \
        '<Project Sdk=''Microsoft.NET.Sdk''>', \
        '  <PropertyGroup>', \
        '    <OutputType>Exe</OutputType>', \
        '    <TargetFramework>net9.0</TargetFramework>', \
        '    <PublishSingleFile>true</PublishSingleFile>', \
        '    <SelfContained>false</SelfContained>', \
        '    <RuntimeIdentifier>win-x64</RuntimeIdentifier>', \
        '    <DebugType>none</DebugType>', \
        '    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>', \
        '  </PropertyGroup>', \
        '  <ItemGroup>', \
        '    <Compile Include=''$(LauncherSource)'' />', \
        '  </ItemGroup>', \
        '</Project>' \
    ); \
    dotnet publish $project --nologo --configuration Release --output $hubPublish "-p:LauncherSource=$hubSource" -p:AssemblyName=unity-hub-launcher; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to publish the Unity Hub launcher' }; \
    Remove-Item -LiteralPath C:\unity\obj -Recurse -Force; \
    dotnet publish $project --nologo --configuration Release --output $composePublish "-p:LauncherSource=$composeSource" -p:AssemblyName=compose-unity; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to publish the compose-unity launcher' }; \
    $hubArtifacts = @(Get-ChildItem -LiteralPath $hubPublish -File); \
    if ($hubArtifacts.Count -ne 1 -or $hubArtifacts[0].Name -ne 'unity-hub-launcher.exe') { throw 'Unity Hub launcher publish did not produce exactly one executable' }; \
    $composeArtifacts = @(Get-ChildItem -LiteralPath $composePublish -File); \
    if ($composeArtifacts.Count -ne 1 -or $composeArtifacts[0].Name -ne 'compose-unity.exe') { throw 'compose-unity launcher publish did not produce exactly one executable' }; \
    $hub = 'C:\Program Files\Unity Hub\Unity Hub.exe'; \
    Move-Item -LiteralPath $hub -Destination 'C:\Program Files\Unity Hub\Unity Hub.real.exe'; \
    Move-Item -LiteralPath $hubArtifacts[0].FullName -Destination $hub; \
    Move-Item -LiteralPath $composeArtifacts[0].FullName -Destination C:\Windows\compose-unity.exe; \
    Remove-Item -LiteralPath $project, $hubSource, $composeSource -Force; \
    Remove-Item -LiteralPath C:\unity\bin, C:\unity\obj, $hubPublish, $composePublish -Recurse -Force

ARG UNITY_TIMEOUT="14400"
RUN compose-unity require slothsoft/unity; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to require slothsoft/unity' }; \
    compose-unity config process-timeout $env:UNITY_TIMEOUT; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to configure the Composer process timeout' }; \
    compose-unity update --no-interaction --no-dev --optimize-autoloader --classmap-authoritative; \
    if ($LASTEXITCODE -ne 0) { throw 'Failed to update compose-unity dependencies' }

# Test
RUN compose-unity exec unity-build; \
    if ($LASTEXITCODE -ne 0) { throw 'compose-unity unity-build smoke check failed' }; \
    compose-unity exec unity-help; \
    if ($LASTEXITCODE -ne 0) { throw 'compose-unity unity-help smoke check failed' }

VOLUME ["C:/Program Files/Unity/Hub/Editor", "C:/Users/ContainerAdministrator/AppData/Roaming/Unity", "C:/Users/ContainerAdministrator/AppData/Roaming/UnityHub", "C:/Users/ContainerAdministrator/AppData/Local/Unity", "C:/ProgramData/Unity"]
