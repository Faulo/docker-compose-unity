param(
    [Parameter(Mandatory)] [string] $Namespace,
    [Parameter(Mandatory)] [string] $Name,
    [Parameter(Mandatory)] [string] $Variant,
    [Parameter(Mandatory)] [string] $Context,
    [Parameter(Mandatory)] [string] $Image,
    [Parameter(Mandatory)] [string] $Os,
    [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $DockerRunArguments
)

BeforeAll {
    . (Join-Path $PSScriptRoot '../.jenkins/Docker.ps1')
}

Describe "SteamCMD contract [$Os, $Image]" {
    It 'restarts Linux SteamCMD and preserves arguments, working directory, and final exit status through docker exec' -Skip:($Os -ne 'linux') {
        $containerName = "compose-unity-steam-$([guid]::NewGuid().ToString('N'))"
        $probe = @'
set -eu
cat > /opt/steamcmd/linux64/steamcmd <<'CLIENT'
#!/bin/sh
if [ ! -f /tmp/steamcmd-restarted ]; then
    touch /tmp/steamcmd-restarted
    exit 42
fi
printf 'cwd=<%s>\n' "$PWD"
printf 'arg=<%s>\n' "$@"
exit 17
CLIENT
chmod +x /opt/steamcmd/linux64/steamcmd
mkdir -p '/tmp/steam working directory'
exec steamcmd '+login' 'user with spaces' '' '+quit'
'@
        try {
            Invoke-Docker -Context $Context -Arguments @('run', '--detach', '--tty', '--name', $containerName, $Image, 'cat')
            $result = Get-DockerCommandResult -Context $Context -Arguments @(
                'exec', '--workdir', '/tmp', $containerName, '/bin/sh', '-c', ($probe -replace "`r`n", "`n")
            )
            $result.ExitCode | Should -Be 17 -Because ($result.Output | Out-String)
            $result = Get-DockerCommandResult -Context $Context -Arguments @(
                'exec', '--workdir', '/tmp/steam working directory', $containerName, '/bin/sh', '-c',
                'rm /tmp/steamcmd-restarted; exec steamcmd ''+login'' ''user with spaces'' '''' ''+quit'''
            )
            $result.ExitCode | Should -Be 17 -Because ($result.Output | Out-String)
            $output = $result.Output | Out-String
            $output | Should -Match 'cwd=</tmp/steam working directory>'
            $output | Should -Match 'arg=<\+login>\s+arg=<user with spaces>\s+arg=<>\s+arg=<\+quit>'
        } finally {
            Get-DockerCommandResult -Context $Context -Arguments @('container', 'rm', '--force', $containerName) | Out-Null
        }
    }

    It 'logs in to Steam with runtime credentials' {
        Invoke-Docker -Context $Context -RunArguments $DockerRunArguments -Arguments @(
            'run', '--rm', '--env', 'STEAM_CREDENTIALS_USR', '--env', 'STEAM_CREDENTIALS_PSW',
            $Image, 'compose-unity', 'exec', 'steam-login'
        )
    }

    It 'initializes and reuses persistent SteamCMD state' {
        $volumeName = "compose-unity-steam-$([guid]::NewGuid().ToString('N'))"
        $volumePath = if ($Os -eq 'windows') { 'C:/steam' } else { '/root/Steam' }
        try {
            foreach ($attempt in 1..2) {
                $result = Get-DockerCommandResult -Context $Context -Arguments @(
                    'run', '--rm', '--volume', "${volumeName}:${volumePath}", $Image, 'steamcmd', '+quit'
                )
                $result.ExitCode | Should -Be 0 -Because "SteamCMD attempt $attempt failed: $($result.Output | Out-String)"
            }
        } finally {
            Get-DockerCommandResult -Context $Context -Arguments @('volume', 'rm', '--force', $volumeName) | Out-Null
        }
    }
}
