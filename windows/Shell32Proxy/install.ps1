param(
    [ValidateNotNullOrEmpty()]
    [string] $OsBase = $env:OS_BASE
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$variantDirectory = Join-Path $PSScriptRoot $OsBase
$system32 = 'C:\Windows\System32\shell32.dll'
$system32Real = 'C:\Windows\System32\shell32real.dll'
$sysWow64 = 'C:\Windows\SysWOW64\shell32.dll'
$sysWow64Real = 'C:\Windows\SysWOW64\shell32real.dll'
$proxyX64 = Join-Path $variantDirectory 'shell32-proxy-x64.dll'
$proxyX86 = Join-Path $variantDirectory 'shell32-proxy-x86.dll'
$exportsX64 = Join-Path $variantDirectory 'shell32-exports-x64.rsp'
$exportsX86 = Join-Path $variantDirectory 'shell32-exports-x86.rsp'
$hashManifest = Join-Path $variantDirectory 'shell32-hashes.psd1'
if (-not (Test-Path -LiteralPath $hashManifest -PathType Leaf)) {
    throw "Unsupported Windows OS base: $OsBase"
}
$hashes = Import-PowerShellDataFile -LiteralPath $hashManifest

$expectedHashes = @{
    $proxyX64 = $hashes.ProxyX64
    $proxyX86 = $hashes.ProxyX86
}
foreach ($path in $expectedHashes.Keys) {
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
    if ($actualHash -ne $expectedHashes[$path]) {
        throw "Unexpected SHELL32 artifact hash for $path`: $actualHash"
    }
}

& (Join-Path $PSScriptRoot 'validate.ps1') -OriginalPath $system32 `
    -ExpectedMachine 0x8664 -ExpectedFileBuilds $hashes.FileBuilds `
    -ExportManifest $exportsX64
& (Join-Path $PSScriptRoot 'validate.ps1') -OriginalPath $sysWow64 `
    -ExpectedMachine 0x014C -ExpectedFileBuilds $hashes.FileBuilds `
    -ExportManifest $exportsX86

foreach ($replacement in @(
    @{ Original = $system32; Real = $system32Real; Proxy = $proxyX64 },
    @{ Original = $sysWow64; Real = $sysWow64Real; Proxy = $proxyX86 }
)) {
    if (Test-Path -LiteralPath $replacement.Real) {
        throw "SHELL32 backing DLL already exists: $($replacement.Real)"
    }
    takeown.exe /F $replacement.Original /A | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to take ownership of $($replacement.Original)"
    }
    icacls.exe $replacement.Original /grant '*S-1-5-32-544:F' | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to grant access to $($replacement.Original)"
    }
    Move-Item -LiteralPath $replacement.Original -Destination $replacement.Real
    Move-Item -LiteralPath $replacement.Proxy -Destination $replacement.Original
}
