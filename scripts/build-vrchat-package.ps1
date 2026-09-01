#requires -Version 7.2

<#
.SYNOPSIS
Builds the VRChat Collector Package into a host directory.

.DESCRIPTION
The Package is built inside a linux container, because the artifact selector is written from
the OS/arch of the process that runs --create-package. Building on a Windows or macOS host
would produce a Package the headless Hub container cannot use.

Prerequisites: Docker Desktop running.
#>
[CmdletBinding()]
param(
    [string] $Output
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $repositoryRoot '.local/collector-packages/vrchat'
}
if (-not [IO.Path]::IsPathRooted($Output)) {
    $Output = Join-Path $repositoryRoot $Output
}
$Output = [IO.Path]::GetFullPath($Output)

$dockerfile = Join-Path $repositoryRoot 'collection/collectors/Heartbeat.Collector.VRChat/Dockerfile'
if (-not (Test-Path -LiteralPath $dockerfile -PathType Leaf)) {
    throw "Dockerfile not found: $dockerfile"
}
if ((Test-Path -LiteralPath $Output) -and -not (Test-Path -LiteralPath $Output -PathType Container)) {
    throw "Output path exists and is not a directory: $Output"
}

$null = Get-Command docker -CommandType Application -ErrorAction Stop

# 残留文件会进入 Package 的 tree hash 并可能让安装校验失败，所以每次都从空目录开始。
Write-Host "[1/2] Clearing $Output..."
if (Test-Path -LiteralPath $Output) {
    Remove-Item -LiteralPath $Output -Recurse -Force
}
$null = New-Item -ItemType Directory -Force -Path $Output

Write-Host '[2/2] Building the VRChat Collector Package...'
Push-Location -LiteralPath $repositoryRoot
try {
    & docker build `
        --file 'collection/collectors/Heartbeat.Collector.VRChat/Dockerfile' `
        --target package `
        --output "type=local,dest=$Output" `
        .
    if ($LASTEXITCODE -ne 0) {
        throw "docker build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath (Join-Path $Output 'collector-manifest.json') -PathType Leaf)) {
    throw "Build finished but collector-manifest.json is missing under $Output."
}

Write-Host "VRChat Collector Package ready: $Output"
Write-Host 'Point the headless Hub at its parent directory as a read-only Package source'
Write-Host '(HEADLESS_PACKAGE_SOURCE_PATH), and keep packageDirectory at /package-source/vrchat.'
