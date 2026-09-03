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

$comparison = if ($IsWindows) {
    [StringComparison]::OrdinalIgnoreCase
}
else {
    [StringComparison]::Ordinal
}
$directorySeparator = [IO.Path]::DirectorySeparatorChar
$repositoryPrefix = $repositoryRoot.TrimEnd($directorySeparator) + $directorySeparator
$outputPrefix = $Output.TrimEnd($directorySeparator) + $directorySeparator
if ($Output -eq [IO.Path]::GetPathRoot($Output) -or
    $repositoryRoot.Equals($Output, $comparison) -or
    $repositoryPrefix.StartsWith($outputPrefix, $comparison)) {
    throw "Refusing unsafe output path: $Output"
}

$dockerfile = Join-Path $repositoryRoot 'collection/collectors/Heartbeat.Collector.VRChat/Dockerfile'
if (-not (Test-Path -LiteralPath $dockerfile -PathType Leaf)) {
    throw "Dockerfile not found: $dockerfile"
}
if ((Test-Path -LiteralPath $Output) -and -not (Test-Path -LiteralPath $Output -PathType Container)) {
    throw "Output path exists and is not a directory: $Output"
}
if ((Test-Path -LiteralPath $Output) -and
    ((Get-Item -LiteralPath $Output -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw "Output path must not be a symbolic link or reparse point: $Output"
}

$null = Get-Command docker -CommandType Application -ErrorAction Stop

$ownershipMarker = '.heartbeat-vrchat-package-output'
$ownershipValue = 'heartbeat-vrchat-package-output-v1'
if (Test-Path -LiteralPath $Output -PathType Container) {
    $hasContent = $null -ne (Get-ChildItem -LiteralPath $Output -Force | Select-Object -First 1)
    $markerPath = Join-Path $Output $ownershipMarker
    $owned = (Test-Path -LiteralPath $markerPath -PathType Leaf) -and
        ((Get-Content -LiteralPath $markerPath -Raw).Trim() -ceq $ownershipValue)
    if ($hasContent -and -not $owned) {
        throw "Output directory is non-empty and not owned by this tool: $Output"
    }
}

$outputParent = Split-Path -Parent $Output
$outputName = Split-Path -Leaf $Output
$null = New-Item -ItemType Directory -Force -Path $outputParent
$stagingDirectory = Join-Path $outputParent ".$outputName.build-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $stagingDirectory

try {
    Write-Host "[1/2] Building the VRChat Collector Package in $stagingDirectory..."
    Push-Location -LiteralPath $repositoryRoot
    try {
        & docker build `
            --file 'collection/collectors/Heartbeat.Collector.VRChat/Dockerfile' `
            --target package `
            --output "type=local,dest=$stagingDirectory" `
            .
        if ($LASTEXITCODE -ne 0) {
            throw "docker build failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory 'collector-manifest.json') -PathType Leaf)) {
        throw 'Build finished but collector-manifest.json is missing from the staged output.'
    }
    Set-Content -LiteralPath (Join-Path $stagingDirectory $ownershipMarker) -Value $ownershipValue

    Write-Host "[2/2] Replacing the tool-owned output at $Output..."
    if (Test-Path -LiteralPath $Output) {
        Remove-Item -LiteralPath $Output -Recurse -Force
    }
    Move-Item -LiteralPath $stagingDirectory -Destination $Output
    $stagingDirectory = $null
}
finally {
    if ($null -ne $stagingDirectory -and (Test-Path -LiteralPath $stagingDirectory)) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}

Write-Host "VRChat Collector Package ready: $Output"
Write-Host 'Use package-vrchat-release.sh or the dedicated tag workflow to publish it to the Registry.'
