param(
    [string] $Context = 'linux',
    [string] $Image = 'tmp/compose-unity-integration:latest',
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\ComposeUnity.Tests\test-files\ValidProject')).Path
$container = 'tmp-compose-unity-project-info-' + [Guid]::NewGuid().ToString('N')
$started = $false

function Invoke-Docker {
    param([Parameter(ValueFromRemainingArguments)] [string[]] $Arguments)

    & docker --context $Context @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Docker command failed with exit code $LASTEXITCODE`: docker --context $Context $($Arguments -join ' ')"
    }
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

    $json = $data[0].Substring(6)
    return $json | ConvertFrom-Json -Depth 20
}

try {
    $daemonOs = (Invoke-Docker info --format '{{.OSType}}' | Select-Object -Last 1).Trim()
    if (-not $SkipBuild) {
        if ($daemonOs -eq 'windows') {
            throw 'The lightweight integration image is Linux-only. Build the production Windows image, then pass -Image and -SkipBuild.'
        }

        Invoke-Docker build --tag $Image --file common/ComposeUnity.IntegrationTests/Dockerfile $repository
    }

    $dockerMount = if ($daemonOs -eq 'windows') {
        $contextHost = (Invoke-Docker context inspect $Context --format '{{.Endpoints.docker.Host}}' | Select-Object -Last 1).Trim()
        if ($contextHost -notmatch '^npipe:/{4}\./pipe/(?<pipe>.+)$') {
            throw "Windows integration tests require a local named-pipe Docker context, but '$Context' uses '$contextHost'."
        }

        "type=npipe,source=\\.\pipe\$($Matches.pipe),target=\\.\pipe\docker_engine"
    } else {
        'type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock'
    }
    if ($daemonOs -eq 'windows') {
        Invoke-Docker run --detach --name $container --env COMPOSE_UNITY_MCP=1 --mount $dockerMount $Image | Out-Null
    } else {
        Invoke-Docker run --detach --name $container --env COMPOSE_UNITY_MCP=1 --publish 127.0.0.1::8080 --mount $dockerMount $Image | Out-Null
    }
    $started = $true

    $deadline = (Get-Date).AddMinutes(2)
    do {
        $health = (Invoke-Docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' $container | Select-Object -Last 1).Trim()
        if ($health -eq 'healthy') {
            break
        }

        if ($health -in 'unhealthy', 'exited', 'dead') {
            Invoke-Docker logs $container
            throw "Integration container became $health."
        }

        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    if ($health -ne 'healthy') {
        Invoke-Docker logs $container
        throw 'Integration container did not become healthy within two minutes.'
    }

    if ($daemonOs -eq 'windows') {
        $containerAddress = (Invoke-Docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' $container | Select-Object -Last 1).Trim()
        if ($containerAddress -notmatch '^\d{1,3}(\.\d{1,3}){3}$') {
            throw "Could not determine the Windows container address: $containerAddress"
        }

        $script:Endpoint = "http://${containerAddress}:8080/mcp"
        $script:McpHeaders = @{ Accept = 'application/json, text/event-stream'; Host = 'localhost' }
    } else {
        $published = (Invoke-Docker port $container 8080/tcp | Select-Object -Last 1).Trim()
        if ($published -notmatch ':(?<port>\d+)$') {
            throw "Could not parse published MCP port: $published"
        }

        $script:Endpoint = "http://127.0.0.1:$($Matches.port)/mcp"
        $script:McpHeaders = @{ Accept = 'application/json, text/event-stream' }
    }
    $initialize = Invoke-Mcp 1 initialize @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'compose-unity-integration'; version = '1' } }
    if ($initialize.result.serverInfo.name -ne 'compose-unity') {
        throw 'MCP initialization returned an unexpected server name.'
    }

    $tools = Invoke-Mcp 2 'tools/list' @{}
    $toolNames = @($tools.result.tools.name | Sort-Object)
    $expectedTools = @('execute_method', 'project_info', 'run_tests')
    if (Compare-Object $expectedTools $toolNames) {
        throw "Unexpected MCP tools: $($toolNames -join ', ')"
    }

    $call = Invoke-Mcp 3 'tools/call' @{ name = 'project_info'; arguments = @{ projectRoot = $project } }
    if ($call.result.isError -eq $true) {
        throw "project_info failed: $($call.result.content.text -join "`n")"
    }

    $result = $call.result.structuredContent.result
    if ($result.projectRoot -ne $project -or
        $result.projectName -ne 'Example Game' -or
        $result.editor.version -ne '6000.3.13f1' -or
        $result.rendering.renderPipeline -ne 'Universal' -or
        $result.packages.custom -ne '1.2.3') {
        throw "project_info returned an unexpected result: $($result | ConvertTo-Json -Depth 20 -Compress)"
    }

    Write-Output "project_info end-to-end test passed for Docker context '$Context'."
} finally {
    if ($started) {
        & docker --context $Context rm --force $container | Out-Null
    }
}
