[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('linux', 'windows')]
    [string] $Os
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Docker.ps1')

$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$state = Join-Path $workspace '.reports/native'
New-Item -ItemType Directory -Path "$state/tools", "$state/tmp", "$state/docker" -Force | Out-Null
$env:DOCKER_CONFIG = "$state/docker"
$endpoint = if ($Os -eq 'windows') { 'npipe:////./pipe/docker_engine' } else { 'unix:///var/run/docker.sock' }
& docker --config "$state/docker" context inspect $Os *> $null
if ($LASTEXITCODE -ne 0) {
    & docker --config "$state/docker" context create $Os --docker "host=$endpoint"
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the native test Docker context' }
}
$Context = $Os
$hostWorkspace = $workspace
$containerId = if ($IsWindows) { $env:COMPUTERNAME.ToLowerInvariant() } else { $env:HOSTNAME }
$inspection = Get-DockerCommandResult -Context $Context -Arguments @('inspect', $containerId)
if ($inspection.ExitCode -eq 0) {
    $agent = ($inspection.Output | Out-String | ConvertFrom-Json)[0]
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $mount = $agent.Mounts | Where-Object {
        $workspace.StartsWith($_.Destination.TrimEnd('/\') + [IO.Path]::DirectorySeparatorChar, $comparison)
    } | Sort-Object { $_.Destination.Length } -Descending | Select-Object -First 1
    if ($null -eq $mount) {
        throw "Agent workspace is not backed by a host mount: $workspace"
    }
    $hostWorkspace = $mount.Source.TrimEnd('/\') + $workspace.Substring($mount.Destination.TrimEnd('/\').Length)
}

# Nested test containers must see the same absolute bind paths as the SDK runner.
$hostState = Join-Path $hostWorkspace '.reports/native'
$dockerCommand = (Get-Command docker -CommandType Application | Select-Object -First 1).Source
if ($IsWindows -and $env:ChocolateyInstall) {
    $chocolateyDocker = Join-Path $env:ChocolateyInstall 'lib/docker-cli/bin/docker.exe'
    if (Test-Path -LiteralPath $chocolateyDocker) {
        $dockerCommand = $chocolateyDocker
    }
}
$toolPath = Join-Path $state "tools/$(Split-Path $dockerCommand -Leaf)"
Remove-Item -LiteralPath $toolPath -Force -ErrorAction SilentlyContinue
$sourceStream = [IO.File]::OpenRead($dockerCommand)
try {
    $targetStream = [IO.File]::Create($toolPath)
    try { $sourceStream.CopyTo($targetStream) } finally { $targetStream.Dispose() }
} finally { $sourceStream.Dispose() }
if (!$IsWindows) {
    & chmod +x $toolPath
    if ($LASTEXITCODE -ne 0) { throw 'Could not make the test Docker CLI executable' }
}

$sdkImage = if ($Os -eq 'windows') { 'mcr.microsoft.com/dotnet/sdk:9.0-nanoserver-1809' } else { 'tmp/compose-unity-native-tests:linux' }
if ($Os -eq 'linux') {
    $build = Get-DockerCommandResult -Context $Context -Arguments @(
        'build', '--file', (Join-Path $PSScriptRoot 'Dockerfile.native-linux'), '--tag', $sdkImage, $PSScriptRoot
    )
    $build.Output | Write-Output
    if ($build.ExitCode -ne 0) { throw 'Could not build the Linux native test runner' }
}
$testPath = if ($Os -eq 'windows') {
    "$hostState/tools;C:\Program Files\dotnet;C:\Windows\system32;C:\Windows"
} else {
    "${hostState}/tools:/usr/share/dotnet:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
}
$runArguments = @(
    'run', '--rm', '--volume', "${hostWorkspace}:${hostWorkspace}", '--workdir', $hostWorkspace,
    '--env', "PATH=$testPath", '--env', "DOCKER_CONFIG=$hostState/docker",
    '--env', "NUGET_PACKAGES=$hostState/nuget", '--env', "DOTNET_CLI_HOME=$hostState/cli",
    '--env', "COMPOSE_UNITY_DAEMON_TEST_REQUIRED_OS=$Os"
)
if ($Os -eq 'windows') {
    # Dende's matching LTSC 2019 kernel supports its existing process isolation.
    $runArguments += @('--user', 'ContainerAdministrator', '--volume', '\\.\pipe\docker_engine:\\.\pipe\docker_engine')
} else {
    $runArguments += @('--network', 'host', '--volume', '/var/run/docker.sock:/var/run/docker.sock')
}

foreach ($project in 'ComposeUnity.Tests', 'ComposeUnity.DaemonTests') {
    $reportPath = Join-Path $workspace ".reports/$project-$Os/TestResults.xml"
    Remove-Item -LiteralPath $reportPath -Force -ErrorAction SilentlyContinue
    $projectArguments = $runArguments
    if ($project -eq 'ComposeUnity.DaemonTests') {
        $projectArguments += @('--env', "TMP=$hostState/tmp", '--env', "TEMP=$hostState/tmp", '--env', "TMPDIR=$hostState/tmp")
    }
    $result = Get-DockerCommandResult -Context $Context -Arguments ($projectArguments + @(
        $sdkImage, 'dotnet', 'test', "common/$project/$project.csproj", '--configuration', 'Release',
        '--logger', "junit;LogFilePath=$hostWorkspace/.reports/$project-$Os/TestResults.xml", '--blame-hang-timeout', '10m'
    ))
    $result.Output | Write-Output
    if ($result.ExitCode -ne 0) {
        throw "$project failed on $Os with exit code $($result.ExitCode)"
    }
    if (!(Test-Path -LiteralPath $reportPath)) {
        throw "$project produced no JUnit report on $Os"
    }
    [xml] $report = Get-Content -LiteralPath $reportPath -Raw
    if ($report.SelectNodes('//testcase[not(skipped)]').Count -le 0) {
        throw "$project executed no tests on $Os"
    }
}
