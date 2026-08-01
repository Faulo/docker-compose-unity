$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$settingsPath = Join-Path $PSScriptRoot "$env:OS_BASE\settings.psd1"
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    throw "Unsupported Windows OS base: $env:OS_BASE"
}
$settings = Import-PowerShellDataFile -LiteralPath $settingsPath

if (-not $settings.InstallBuildTools) {
    Write-Host 'Skipping Visual Studio Build Tools for this Windows base.'
    return
}

$bootstrapper = 'C:\vs_buildtools.exe'
$installPath = 'C:\BuildTools'
Invoke-WebRequest -Uri 'https://aka.ms/vs/17/release/vs_buildtools.exe' -OutFile $bootstrapper
$arguments = @(
    '--quiet',
    '--wait',
    '--norestart',
    '--nocache',
    '--installPath', $installPath,
    '--add', 'Microsoft.VisualStudio.Workload.MSBuildTools',
    '--add', 'Microsoft.VisualStudio.Component.NuGet.BuildTools'
)
$process = Start-Process -FilePath $bootstrapper -ArgumentList $arguments -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "Visual Studio Build Tools installer failed with exit code $($process.ExitCode)"
}
Remove-Item -LiteralPath $bootstrapper -Force

$msbuild = Join-Path $installPath 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) {
    throw "MSBuild executable not found: $msbuild"
}
& $msbuild -version
if ($LASTEXITCODE -ne 0) {
    throw 'MSBuild smoke check failed'
}
