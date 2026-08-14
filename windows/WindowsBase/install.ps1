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
$bootstrapperUri = 'https://download.visualstudio.microsoft.com/download/pr/d93bcdb2-1c87-4eba-9ee3-734d20b5a8f3/b9af11ca8513c7ce2c6906261243bda72f645db1701e3cf7bf1e8d535c594d6f/vs_BuildTools.exe'
Invoke-WebRequest -Uri $bootstrapperUri -OutFile $bootstrapper
$actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $bootstrapper).Hash
if ($actualHash -ne 'B9AF11CA8513C7CE2C6906261243BDA72F645DB1701E3CF7BF1E8D535C594D6F') {
    throw "Visual Studio Build Tools bootstrapper checksum mismatch: $actualHash"
}
$arguments = @(
    '--quiet',
    '--wait',
    '--norestart',
    '--nocache',
    '--installPath', $installPath,
    '--add', 'Microsoft.VisualStudio.Workload.MSBuildTools',
    '--add', 'Microsoft.VisualStudio.Component.NuGet.BuildTools'
)
$process = Start-Process -FilePath $bootstrapper -ArgumentList $arguments -PassThru
if (-not $process.WaitForExit(7200000)) {
    taskkill.exe /PID $process.Id /T /F | Out-Null
    throw 'Visual Studio Build Tools installer exceeded the two-hour timeout'
}
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
