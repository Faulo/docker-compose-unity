param(
    [Parameter(Mandatory)] [string] $Context,
    [Parameter(Mandatory)] [ValidateSet('linux', 'windows')] [string] $ExpectedOs
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\ComposeUnity.Tests\test-files\ValidProject')).Path
$id = [Guid]::NewGuid().ToString('N')
$container = "tmp-compose-unity-daemon-$id"
$image = "tmp/compose-unity-daemon-tests:$id"
$staging = Join-Path ([System.IO.Path]::GetTempPath()) "compose-unity-daemon-tests-$id"
$started = $false
$built = $false
$controllerId = $null

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [string] $Executable,
        [Parameter(ValueFromRemainingArguments)] [string[]] $Arguments
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable failed with exit code $LASTEXITCODE`: $($Arguments -join ' ')"
    }
}

function Invoke-Docker {
    param([Parameter(ValueFromRemainingArguments)] [string[]] $Arguments)

    Invoke-Checked docker --context $Context @Arguments
}

function Invoke-Mcp {
    param(
        [int] $Id,
        [string] $Method,
        [hashtable] $Parameters
    )

    $body = @{ jsonrpc = '2.0'; id = $Id; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 20 -Compress
    $response = Invoke-WebRequest -Uri $script:Endpoint -Method Post -Headers $script:McpHeaders -ContentType 'application/json' -Body $body
    $data = @($response.Content -split "`n" | Where-Object { $_.StartsWith('data: ', [StringComparison]::Ordinal) })
    if ($data.Count -ne 1) {
        throw "Unexpected MCP response: $($response.Content)"
    }

    return $data[0].Substring(6) | ConvertFrom-Json -Depth 20
}

try {
    New-Item -ItemType Directory -Path (Join-Path $staging 'controller'), (Join-Path $staging 'backend') | Out-Null
    $runtime = if ($ExpectedOs -eq 'windows') { 'win-x64' } else { 'linux-x64' }
    Invoke-Checked dotnet publish (Join-Path $repository 'common\ComposeUnity\ComposeUnity.csproj') --nologo --configuration Release --runtime $runtime --self-contained true --output (Join-Path $staging 'controller')
    Invoke-Checked dotnet publish (Join-Path $repository 'common\ComposeUnity.DaemonTests.Backend\ComposeUnity.DaemonTests.Backend.csproj') --nologo --configuration Release --runtime $runtime --self-contained true --output (Join-Path $staging 'backend')

    $dockerfile = Join-Path $PSScriptRoot "Dockerfile.$ExpectedOs"
    Invoke-Docker build --tag $image --file $dockerfile $staging
    $built = $true

    if ($ExpectedOs -eq 'windows') {
        $contextHost = (Invoke-Checked docker context inspect $Context --format '{{.Endpoints.docker.Host}}' | Select-Object -Last 1).Trim()
        if ($contextHost -notmatch '^npipe:/{4}\./pipe/(?<pipe>.+)$') {
            throw "Windows daemon tests require a named-pipe context, but '$Context' uses '$contextHost'."
        }

        $dockerMount = "type=npipe,source=\\.\pipe\$($Matches.pipe),target=\\.\pipe\docker_engine"
        Invoke-Docker run --detach --name $container --env COMPOSE_UNITY_MCP=1 --mount $dockerMount $image | Out-Null
    } else {
        $dockerMount = 'type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock'
        Invoke-Docker run --detach --name $container --env COMPOSE_UNITY_MCP=1 --publish 127.0.0.1::8080 --mount $dockerMount $image | Out-Null
    }
    $started = $true
    $controllerId = (Invoke-Docker inspect --format '{{.Id}}' $container | Select-Object -Last 1).Trim()

    $deadline = (Get-Date).AddMinutes(2)
    do {
        $health = (Invoke-Docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' $container | Select-Object -Last 1).Trim()
        if ($health -eq 'healthy') {
            break
        }
        if ($health -in 'unhealthy', 'exited', 'dead') {
            Invoke-Docker logs $container
            throw "Daemon-test controller became $health."
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    if ($health -ne 'healthy') {
        Invoke-Docker logs $container
        throw 'Daemon-test controller did not become healthy within two minutes.'
    }

    if ($ExpectedOs -eq 'windows') {
        $address = (Invoke-Docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' $container | Select-Object -Last 1).Trim()
        if ($address -notmatch '^\d{1,3}(\.\d{1,3}){3}$') {
            throw "Could not determine the Windows container address: $address"
        }
        $script:Endpoint = "http://${address}:8080/mcp"
        $script:McpHeaders = @{ Accept = 'application/json, text/event-stream'; Host = 'localhost' }
    } else {
        $published = (Invoke-Docker port $container 8080/tcp | Select-Object -Last 1).Trim()
        if ($published -notmatch ':(?<port>\d+)$') {
            throw "Could not parse the published MCP port: $published"
        }
        $script:Endpoint = "http://127.0.0.1:$($Matches.port)/mcp"
        $script:McpHeaders = @{ Accept = 'application/json, text/event-stream' }
    }

    $initialize = Invoke-Mcp 1 initialize @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'compose-unity-daemon-tests'; version = '1' } }
    if ($initialize.result.serverInfo.name -ne 'compose-unity') {
        throw 'MCP initialization returned an unexpected server name.'
    }

    $info = Invoke-Mcp 2 'tools/call' @{ name = 'project_info'; arguments = @{ projectRoot = $project } }
    if ($info.result.isError -eq $true -or $info.result.structuredContent.result.projectName -ne 'Example Game') {
        throw "project_info returned an unexpected result: $($info | ConvertTo-Json -Depth 20 -Compress)"
    }

    $arguments = @('', 'two words', '--', '"quoted"')
    $method = Invoke-Mcp 3 'tools/call' @{ name = 'execute_method'; arguments = @{ projectRoot = $project; method = 'DaemonTests.Arguments'; arguments = $arguments } }
    if ($method.result.isError -eq $true) {
        throw "execute_method failed: $($method | ConvertTo-Json -Depth 20 -Compress)"
    }
    $methodResult = $method.result.structuredContent.result
    $backendResult = $methodResult.output | ConvertFrom-Json -Depth 10
    if ($methodResult.exitStatus -ne 7 -or $methodResult.errorOutput -ne 'daemon-test stderr' -or $backendResult.method -ne 'DaemonTests.Arguments' -or (Compare-Object $arguments @($backendResult.arguments))) {
        throw "execute_method did not preserve its result and arguments: $($methodResult | ConvertTo-Json -Depth 20 -Compress)"
    }

    $workerBefore = @(Invoke-Docker ps --all --quiet --filter "label=net.slothsoft.compose-unity.kind=worker" --filter "label=net.slothsoft.compose-unity.controller=$controllerId")
    if ($workerBefore.Count -ne 1) {
        throw "Expected one retained worker, found $($workerBefore.Count)."
    }
    $methodAgain = Invoke-Mcp 4 'tools/call' @{ name = 'execute_method'; arguments = @{ projectRoot = $project; method = 'DaemonTests.Reuse'; arguments = @() } }
    $workerAfter = @(Invoke-Docker ps --all --quiet --filter "label=net.slothsoft.compose-unity.kind=worker" --filter "label=net.slothsoft.compose-unity.controller=$controllerId")
    if ($methodAgain.result.isError -eq $true -or $workerAfter.Count -ne 1 -or $workerAfter[0] -ne $workerBefore[0]) {
        throw 'The retained worker was not reused.'
    }

    $tests = Invoke-Mcp 5 'tools/call' @{ name = 'run_tests'; arguments = @{ projectRoot = $project; modes = @('EditMode', 'Play Mode') } }
    $testResult = $tests.result.structuredContent.result
    if ($tests.result.isError -eq $true -or $testResult.outcome -ne 'passed' -or $testResult.counts.total -ne 2 -or $testResult.counts.passed -ne 2) {
        throw "run_tests returned an unexpected result: $($tests | ConvertTo-Json -Depth 20 -Compress)"
    }

    $mounts = Invoke-Docker inspect --format '{{json .Mounts}}' $workerBefore[0] | ConvertFrom-Json -Depth 20
    $projectMounts = @($mounts | Where-Object { $_.Destination -match '(Assets|Packages|ProjectSettings)$' })
    $dockerMounts = @($mounts | Where-Object { $_.Destination -in '/var/run/docker.sock', '\\.\pipe\docker_engine' })
    if ($projectMounts.Count -ne 3 -or $dockerMounts.Count -ne 0) {
        throw 'The retained worker has unexpected project or Docker endpoint mounts.'
    }

    Write-Output "Daemon tests passed for Docker context '$Context'."
} finally {
    if ($controllerId) {
        $owned = @(& docker --context $Context ps --all --quiet --filter "label=net.slothsoft.compose-unity.controller=$controllerId")
        foreach ($ownedContainer in $owned) {
            if (-not [string]::IsNullOrWhiteSpace($ownedContainer)) {
                & docker --context $Context rm --force $ownedContainer | Out-Null
            }
        }
    }
    if ($started) {
        & docker --context $Context rm --force $container | Out-Null
    }
    if ($built) {
        & docker --context $Context image rm --force $image | Out-Null
    }
    if (Test-Path -LiteralPath $staging) {
        $resolvedStaging = (Resolve-Path -LiteralPath $staging).Path
        $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $resolvedStaging.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or -not (Split-Path -Leaf $resolvedStaging).StartsWith('compose-unity-daemon-tests-', [StringComparison]::Ordinal)) {
            throw "Refusing to remove unexpected staging directory: $resolvedStaging"
        }
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
